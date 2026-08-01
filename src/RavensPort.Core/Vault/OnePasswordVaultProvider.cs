using System.Text.Json;
using System.Text.Json.Nodes;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// The store, backed by the 1Password CLI (<c>op</c>).
///
/// Every write goes through a JSON item template on stdin rather than <c>field=value</c>
/// arguments. Both forms are documented and the argument form is far more convenient, but a
/// Windows process command line is readable by any process in the session — putting a client
/// secret there would make this strictly worse than the encrypted local file it replaces.
/// </summary>
/// <param name="exePathOverride">
/// Skips the search for the binary. For tests, which must not depend on whether the real CLI
/// happens to be installed — and must not reach for the process-wide environment variable, since
/// test classes run in parallel and would clobber each other's.
/// </param>
public sealed class OnePasswordVaultProvider(
    ICliRunner cliRunner,
    ActivityLog activityLog,
    string? exePathOverride = null) : IConfigVault
{
    /// <summary>Fields that can legitimately become absent, and so must be actively cleared.</summary>
    private static readonly string[] ClearableSecretFields =
    [
        VaultFields.ApiKey, VaultFields.AccessToken, VaultFields.RefreshToken,
        VaultFields.TokenType, VaultFields.ExpiresAtUtc, VaultFields.ObtainedUtc,
    ];

    private string? _exePath;
    private string? _vaultId;
    private string _vaultName = VaultConstants.VaultName;
    private long _loadedRevision;

    /// <summary>
    /// Set by <see cref="Forget"/>, cleared when the user names a vault again. Without it a probe
    /// after disconnecting would rediscover the vault the user has just left and reattach to it.
    /// </summary>
    private bool _discoveryDisabled;

    public VaultBackendKind Kind => VaultBackendKind.OnePassword;

    public string VaultName => _vaultName;

    public string? LastLoadWarning { get; private set; }

    public IReadOnlyList<string> LastLoadRemovals { get; private set; } = [];

    /// <summary>
    /// Optional service-account token, for a machine that should never show an unlock prompt.
    /// Passed in the child's environment, never as an argument.
    /// </summary>
    public string? ServiceAccountToken { get; set; }

    public async Task<VaultStatus> ProbeAsync(CancellationToken ct = default)
    {
        _exePath = exePathOverride ?? VaultProbe.FindOnePassword();
        if (_exePath is null || !File.Exists(_exePath)) return VaultStatus.NotInstalled(Kind);

        Version? version;
        try
        {
            var versionResult = await RunAsync(["--version"], ct: ct);
            if (!versionResult.Succeeded)
            {
                return VaultStatus.Faulted(Kind, versionResult.FirstErrorLine(), _exePath);
            }

            version = VaultProbe.ParseVersion(versionResult.StdOut);
        }
        catch (VaultCliException ex)
        {
            return VaultStatus.Faulted(Kind, ex.Message, _exePath);
        }

        if (version is not null && version < VaultProbe.MinimumOnePasswordVersion)
        {
            return VaultStatus.Faulted(Kind,
                $"1Password CLI {version} is too old — {VaultProbe.MinimumOnePasswordVersion} or newer is required.",
                _exePath);
        }

        CliResult vaultList;
        try
        {
            vaultList = await RunAsync(["vault", "list", "--format", "json"], ct: ct);
        }
        catch (VaultCliException ex)
        {
            return VaultStatus.Faulted(Kind, ex.Message, _exePath);
        }

        if (!vaultList.Succeeded)
        {
            // Everything that is not a working session lands here: locked, signed out, desktop-app
            // integration turned off, a service-account token that has expired. They are one state
            // as far as the app is concerned — "you need to authenticate" — and the CLI's own
            // wording is more accurate than anything guessed from an exit code.
            return new VaultStatus(Kind, VaultAvailability.NotSignedIn,
                _exePath, version?.ToString(), Detail: vaultList.FirstErrorLine());
        }

        var vaults = ParseVaults(vaultList.StdOut);
        _vaultId = vaults.FirstOrDefault(v => v.Name == _vaultName)?.VaultId;

        List<OnePasswordVault> configured = [];

        // Skipped once the user has disconnected. Rediscovery is what makes a vault stick across
        // restarts, and straight after a disconnect it would silently reattach the very vault they
        // just stepped away from — leaving them no way to pick a different one.
        if (_vaultId is null && !_discoveryDisabled)
        {
            configured = await FindConfiguredVaultsAsync(vaults, ct);

            if (configured.Count == 1)
            {
                // A vault the user pointed RavensPort at is not remembered on this PC — nothing
                // about this app is — so it is found the same way the backend itself is: whichever
                // vault actually holds the configuration is the one that was being used.
                _vaultName = configured[0].Name;
                _vaultId = configured[0].VaultId;

                activityLog.Log($"VAULT 1Password — using the existing '{_vaultName}' vault, "
                                + "which holds the RavensPort configuration");
            }
        }

        return new VaultStatus(
            Kind,
            Resolve(),
            _exePath,
            version?.ToString(),
            _vaultId,
            VaultName: _vaultName,
            Vaults: [.. vaults.Select(v => v.Name)],
            ConfiguredVaults: [.. configured.Select(v => v.Name)],
            AdoptableVaults: await FindAdoptableVaultsAsync(vaults, configured, ct));

        // More than one configured vault is separate profiles, and opening one would mean
        // overwriting the other's note on the next save. That is a question for the user.
        VaultAvailability Resolve() =>
            _vaultId is not null ? VaultAvailability.Ready
            : configured.Count > 1 ? VaultAvailability.VaultChoiceNeeded
            : VaultAvailability.VaultMissing;
    }

    public async Task CreateVaultAsync(string vaultName, CancellationToken ct = default)
    {
        var name = vaultName.Trim();
        if (name.Length == 0) throw VaultAdoption.NameRequired();

        RequireExe();

        var listed = await RunAsync(["vault", "list", "--format", "json"], ct: ct);
        if (!listed.Succeeded) throw new VaultLockedException(Kind, listed.FirstErrorLine());

        // Refused rather than silently reused: "create" and "take over what is already there" are
        // different intentions, and the second one has rules — see VaultAdoption.
        if (ParseVaults(listed.StdOut).Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new VaultAdoptionException(
                $"There is already a vault called '{name}'. Choose it under \"use a vault you already have\" "
                + "instead, or pick a different name.");
        }

        var result = await RunAsync(
            ["vault", "create", name, "--description", VaultConstants.VaultDescription, "--format", "json"],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"Could not create the '{name}' vault: {result.FirstErrorLine()}",
                partiallyApplied: false);
        }

        _vaultId = ReadString(JsonNode.Parse(result.StdOut), "id")
                   ?? throw new VaultSaveException(
                       "1Password created the vault but did not report its id.", partiallyApplied: false);

        _vaultName = name;
        _loadedRevision = 0;
        _discoveryDisabled = false;

        // Stamped straight away: the config item is what identifies this vault as RavensPort's on
        // the next launch, and the name the user just chose is not written down anywhere on this PC.
        await SaveAsync(new ConfigStore(), ct);

        activityLog.Log($"VAULT 1Password — created the '{_vaultName}' vault");
    }

    /// <summary>
    /// Takes over a vault the user already has. See <see cref="VaultAdoption"/> for why only an
    /// empty vault or one RavensPort has written to is accepted.
    /// </summary>
    public async Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default)
    {
        var name = vaultName.Trim();
        if (name.Length == 0) throw VaultAdoption.NameRequired();

        RequireExe();

        var listed = await RunAsync(["vault", "list", "--format", "json"], ct: ct);
        if (!listed.Succeeded) throw new VaultLockedException(Kind, listed.FirstErrorLine());

        var vaults = ParseVaults(listed.StdOut);

        // Case-insensitive, because the user is typing a name they read in the 1Password UI and
        // being told "no such vault" over capitalisation would be a poor way to spend their time.
        var match = vaults.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw VaultAdoption.NoSuchVault(name, vaults.Select(v => v.Name));

        var items = await ListItemsAsync(match.VaultId, match.Name, ct);
        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);

        string? note = null;
        if (noteSummary is not null)
        {
            note = (await GetItemAsync(noteSummary.ItemId, match.VaultId, ct))?.Field(VaultFields.NoteContent) ?? "";
        }

        var outcome = VaultAdoption.Judge(match.Name, [.. items.Select(item => item.Title)], note);

        _vaultName = match.Name;
        _vaultId = match.VaultId;
        _loadedRevision = 0;
        _discoveryDisabled = false;
        LastLoadWarning = null;

        if (outcome == VaultAdoptionOutcome.Empty)
        {
            // Stamped now rather than on the first real edit: the config item is the only thing
            // that identifies this vault as RavensPort's next launch, and the name the user just
            // typed is deliberately not written down anywhere on this PC.
            await SaveAsync(new ConfigStore(), ct);
        }

        activityLog.Log($"VAULT 1Password — using the existing '{_vaultName}' vault");
    }

    public void Forget()
    {
        _vaultId = null;
        _vaultName = VaultConstants.VaultName;
        _loadedRevision = 0;
        LastLoadRemovals = [];
        LastLoadWarning = null;

        // Until the user names a vault again. Otherwise the next probe finds the vault holding the
        // configuration — the one they just disconnected from — and quietly picks it up again.
        _discoveryDisabled = true;
    }

    public async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
    {
        LastLoadWarning = null;
        LastLoadRemovals = [];

        await RequireVaultAsync(ct);

        var items = await ListOwnedSummariesAsync(ct);

        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);
        if (noteSummary is null)
        {
            // No note means nothing has ever been saved here. An empty store is the correct
            // answer, and the setup page has already confirmed the vault itself exists.
            _loadedRevision = 0;
            return new ConfigStore();
        }

        var noteItem = await GetItemAsync(noteSummary.ItemId, ct);
        var document = VaultDocument.TryParse(noteItem?.Field(VaultFields.NoteContent) ?? "");

        if (document is null)
        {
            // The note is free text the user can open and edit in 1Password, so a broken one is a
            // mistake rather than corruption. Coming up empty is recoverable; refusing to start is
            // not, and the old configuration is still sitting in the vault to be repaired.
            LastLoadWarning = $"The '{VaultItemNaming.ConfigTitle}' item could not be read as configuration, "
                              + "so RavensPort started with nothing. The item has not been changed.";
            _loadedRevision = 0;
            return new ConfigStore();
        }

        if (document.IsFromANewerLayout)
        {
            LastLoadWarning = $"The vault was written by a newer version of RavensPort "
                              + $"(layout {document.VaultLayoutVersion}). Some settings may not be understood.";
        }

        _loadedRevision = document.Revision;

        var secrets = await ResolveSecretsAsync(document.Index, items, ct);
        var report = new VaultLoadReport();
        var store = VaultMapper.ComposeStore(document, secrets, report);

        LastLoadRemovals = report.Removals;

        if (report.HasAnything)
        {
            LastLoadWarning = string.Join(" ", new[] { LastLoadWarning, report.Message }
                .Where(w => !string.IsNullOrEmpty(w)));
        }

        return store;
    }

    /// <summary>
    /// Already a full rewrite: every edit sends the whole template, including empty values for
    /// fields that should go away, so there is nothing for a forced version to do differently.
    /// </summary>
    public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) => SaveAsync(store, ct);

    /// <summary>Every live item in the vault, ours and the user's. No item contents are fetched.</summary>
    public async Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default)
    {
        await RequireVaultAsync(ct);

        var items = await ListItemsAsync(_vaultId!, _vaultName, ct);

        return [.. items.Select(item => VaultItemEntry.Classify(item.ItemId, item.Title))];
    }

    public async Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        await RequireVaultAsync(ct);

        var result = await RunAsync(
            ["item", "delete", itemId, "--vault", _vaultId!], timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"1Password would not delete the item: {result.FirstErrorLine()}", partiallyApplied: false);
        }
    }

    public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
    {
        await RequireVaultAsync(ct);

        var items = await ListOwnedSummariesAsync(ct);
        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);

        // Two indexes, not one. The previous is what finds the item a record already has; the new
        // one is what the note gets, and it holds only what this save actually wrote. Carrying the
        // old entries forward left the note pointing at items that had been deleted — a dangling
        // reference the design is supposed to make impossible, and a wasted fetch on every load.
        var previousIndex = await ReadIndexAsync(noteSummary, items, ct);
        var index = new VaultIndex();

        var written = 0;
        var secretItems = VaultMapper.BuildSecretItems(store, previousIndex);

        foreach (var item in secretItems)
        {
            ct.ThrowIfCancellationRequested();

            var existingId = ResolveExistingItem(items, item);

            try
            {
                var itemId = existingId is null
                    ? await CreateItemAsync(item.Spec, ct)
                    : await EditItemAsync(existingId, item.Spec, ct);

                index.For(item.Role)[item.RecordId] = itemId;
                written++;
            }
            catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
            {
                throw new VaultSaveException(
                    $"Could not save '{item.Spec.Title}' to 1Password: {ex.Message}",
                    partiallyApplied: written > 0,
                    ex);
            }
        }

        // The note goes last, carrying the index the writes above produced. A crash before this
        // point leaves orphan items that the next save sweeps; a crash after leaves a note one
        // revision behind. Neither leaves the note pointing at an item that does not exist.
        try
        {
            var note = VaultMapper.BuildConfigNote(store, index, _loadedRevision + 1);

            if (noteSummary is null)
            {
                await CreateItemAsync(note, ct);
            }
            else
            {
                await EditItemAsync(noteSummary.ItemId, note, ct);
            }

            _loadedRevision++;
        }
        catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
        {
            throw new VaultSaveException(
                $"Could not save the configuration item to 1Password: {ex.Message}",
                partiallyApplied: written > 0,
                ex);
        }

        await ReconcileDeletionsAsync(items, secretItems, ct);
    }

    // ---- CLI calls ------------------------------------------------------------------------------

    private async Task<string> CreateItemAsync(VaultItemSpec spec, CancellationToken ct)
    {
        var result = await RunAsync(
            ["item", "create", "--vault", _vaultId!, "--format", "json", "-"],
            stdin: BuildTemplate(spec, includeClears: false).ToJsonString(),
            timeout: CliRunner.WriteTimeout,
            ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(result.FirstErrorLine(), partiallyApplied: false);
        }

        return ReadString(JsonNode.Parse(result.StdOut), "id")
               ?? throw new VaultSaveException("1Password created the item but did not report its id.", false);
    }

    private async Task<string> EditItemAsync(string itemId, VaultItemSpec spec, CancellationToken ct)
    {
        // includeClears: an edit merges rather than replaces, so a field that has legitimately gone
        // away — the access token of a credential the user just disconnected — would otherwise sit
        // in the vault forever. Sending it as empty is what actually revokes it from the item.
        var result = await RunAsync(
            ["item", "edit", itemId, "--vault", _vaultId!, "--format", "json", "-"],
            stdin: BuildTemplate(spec, includeClears: true).ToJsonString(),
            timeout: CliRunner.WriteTimeout,
            ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(result.FirstErrorLine(), partiallyApplied: false);
        }

        return itemId;
    }

    /// <summary>
    /// Items in the active vault that this app owns. Everything else in the vault is the user's and
    /// is never read, never written, and — crucially — never a candidate for deletion.
    /// </summary>
    private async Task<List<VaultItemSummary>> ListOwnedSummariesAsync(CancellationToken ct)
    {
        var items = await ListItemsAsync(_vaultId!, _vaultName, ct);
        return items.Where(i => VaultItemNaming.IsOwned(i.Title)).ToList();
    }

    /// <summary>
    /// Everything in a vault, owned or not. The unfiltered count is what decides whether a vault
    /// the user named is empty enough to take over, so this deliberately does not filter.
    /// </summary>
    private async Task<List<VaultItemSummary>> ListItemsAsync(string vaultId, string vaultLabel, CancellationToken ct)
    {
        var result = await RunAsync(["item", "list", "--vault", vaultId, "--format", "json"], ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultCliException($"Could not list the '{vaultLabel}' vault: {result.FirstErrorLine()}");
        }

        var items = new List<VaultItemSummary>();

        foreach (var node in JsonNode.Parse(result.StdOut) as JsonArray ?? [])
        {
            var id = ReadString(node, "id");
            var title = ReadString(node, "title");

            // An archived item is one the user has put away. Reading it would make a vault they
            // had cleared look full and a credential they had removed look present — the same
            // trap Proton Pass's trash sets. Only items with no state at all are live.
            if (ReadString(node, "state") is { Length: > 0 }) continue;

            if (id is not null && title is not null) items.Add(new VaultItemSummary(id, title));
        }

        return items;
    }

    private Task<VaultItemContents?> GetItemAsync(string itemId, CancellationToken ct) =>
        GetItemAsync(itemId, _vaultId!, ct);

    private async Task<VaultItemContents?> GetItemAsync(string itemId, string vaultId, CancellationToken ct)
    {
        var result = await RunAsync(["item", "get", itemId, "--vault", vaultId, "--format", "json"], ct: ct);

        // A miss is normal: the listing and the fetch are separate calls, and an item can be
        // deleted between them by the user or another machine.
        if (!result.Succeeded) return null;

        var node = JsonNode.Parse(result.StdOut);
        if (node is null) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in node["fields"] as JsonArray ?? [])
        {
            var value = ReadString(field, "value");
            if (value is null) continue;

            // Keyed by id, which is what the template sets, with the label as a fallback for a
            // field 1Password rewrote or a user added by hand in its UI.
            if (ReadString(field, "id") is { Length: > 0 } id) fields[id] = value;
            if (ReadString(field, "label") is { Length: > 0 } label) fields.TryAdd(label, value);
        }

        return new VaultItemContents(itemId, ReadString(node, "title") ?? "", fields);
    }

    private async Task ReconcileDeletionsAsync(
        List<VaultItemSummary> existing, List<VaultSecretItem> live, CancellationToken ct)
    {
        var keep = live.Select(i => (i.Role, i.RecordId)).ToHashSet();

        foreach (var item in existing)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out var id)) continue;
            if (role == VaultItemRole.Config || keep.Contains((role, id))) continue;

            var result = await RunAsync(
                ["item", "delete", item.ItemId, "--vault", _vaultId!],
                timeout: CliRunner.WriteTimeout, ct: ct);

            if (!result.Succeeded)
            {
                // Deliberately not fatal. The store itself is already saved; a leftover item is
                // untidy, not wrong, and the next save tries again. Failing here would report a
                // successful save as a failure and trigger a pointless rollback.
                activityLog.Log($"VAULT could not delete '{item.Title}': {result.FirstErrorLine()}");
            }
        }
    }

    // ---- Templates and parsing ------------------------------------------------------------------

    /// <summary>
    /// The item as 1Password's JSON template. Built with JsonNode rather than string
    /// concatenation so a value containing a quote or a backslash cannot break out of the
    /// document — the field values here are user-supplied secrets and names.
    /// </summary>
    private JsonNode BuildTemplate(VaultItemSpec spec, bool includeClears)
    {
        var fields = new JsonArray();
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in spec.Fields)
        {
            present.Add(field.Name);
            fields.Add(BuildField(field.Name, field.Value));
        }

        if (includeClears)
        {
            foreach (var name in ClearableSecretFields.Where(n => !present.Contains(n)))
            {
                fields.Add(BuildField(name, ""));
            }
        }

        if (spec.Caption is { Length: > 0 } caption && !present.Contains(VaultFields.NoteContent))
        {
            fields.Add(BuildField(VaultFields.NoteContent, caption));
        }

        return new JsonObject
        {
            ["title"] = spec.Title,
            ["category"] = CategoryName(spec.Category),
            ["vault"] = new JsonObject { ["id"] = _vaultId },
            ["fields"] = fields,
        };
    }

    private static JsonObject BuildField(string name, string value)
    {
        var field = new JsonObject
        {
            ["id"] = name,
            ["label"] = name,
            ["type"] = VaultFields.IsConcealed(name) ? "CONCEALED" : "STRING",
            ["value"] = value,
        };

        // Purpose is what makes 1Password treat these as the item's real username, password, and
        // notes rather than three custom fields that happen to be named that way — it is the
        // difference between an item the user can actually use and an opaque blob.
        var purpose = name switch
        {
            VaultFields.Username => "USERNAME",
            VaultFields.Password => "PASSWORD",
            VaultFields.NoteContent => "NOTES",
            _ => null,
        };

        if (purpose is not null) field["purpose"] = purpose;

        return field;
    }

    /// <summary>
    /// The category as <c>op</c> writes it in a template — the enum code, not the display name.
    ///
    /// Checked against <c>op item template get</c> on 2.34.1, which emits "SECURE_NOTE", "LOGIN"
    /// and "PASSWORD". Sending the display names ("Secure Note") makes op read no category at all
    /// and refuse the item with <c>"" is not a recognized item category</c> — listing the display
    /// names in the error, which sends you looking in exactly the wrong direction.
    /// </summary>
    private static string CategoryName(VaultItemCategory category) => category switch
    {
        VaultItemCategory.SecureNote => "SECURE_NOTE",
        VaultItemCategory.Login => "LOGIN",
        VaultItemCategory.Password => "PASSWORD",
        _ => "SECURE_NOTE",
    };

    private async Task<VaultIndex> ReadIndexAsync(
        VaultItemSummary? noteSummary, List<VaultItemSummary> items, CancellationToken ct)
    {
        if (noteSummary is null) return new VaultIndex();

        var note = await GetItemAsync(noteSummary.ItemId, ct);
        var document = VaultDocument.TryParse(note?.Field(VaultFields.NoteContent) ?? "");

        // Deliberately no revision comparison. This app assumes a single instance, and the sync
        // queue re-reads and rewrites whenever it can — so a guard here would turn a lock that
        // lifted at an awkward moment into a save that refuses and retries forever, a worse
        // outcome than the concurrent write it would be guarding against. The revision is still
        // stamped into the note, so a second writer is at least visible after the fact.
        //
        // An unreadable note yields an empty index rather than throwing: the guids in the item
        // titles are enough to reconnect everything, and refusing to save would strand the user
        // with a broken note they could only fix by hand.
        return document?.Index ?? new VaultIndex();
    }

    private async Task<Dictionary<(VaultItemRole, Guid), VaultItemContents>> ResolveSecretsAsync(
        VaultIndex index, List<VaultItemSummary> items, CancellationToken ct)
    {
        var wanted = new Dictionary<(VaultItemRole, Guid), string>();

        foreach (var role in new[] { VaultItemRole.Credential, VaultItemRole.RouteKey, VaultItemRole.FunnelKey })
        {
            foreach (var (recordId, itemId) in index.For(role)) wanted[(role, recordId)] = itemId;
        }

        // The index is only a cache. Anything it missed — a note restored from an older version,
        // an item recreated by hand — is recovered from the guid in the title.
        foreach (var item in items)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out var id)) continue;
            if (role == VaultItemRole.Config) continue;

            wanted.TryAdd((role, id), item.ItemId);
        }

        var resolved = new Dictionary<(VaultItemRole, Guid), VaultItemContents>();

        var tasks = wanted.Select(async pair =>
        {
            var ((role, id), itemId) = pair;
            var contents = await GetItemAsync(itemId, ct);
            return (role, id, contents);
        });

        var results = await Task.WhenAll(tasks);

        foreach (var (role, id, contents) in results)
        {
            if (contents is not null) resolved[(role, id)] = contents;
        }

        return resolved;
    }

    /// <summary>
    /// The item this record should be written over, or null to create a fresh one.
    ///
    /// The index is checked <em>against the live listing</em> rather than trusted. An item the note
    /// points at can be gone — deleted in 1Password's own UI, or by the integrity check — and
    /// editing an id that no longer exists fails the whole save with "isn't an item". That made
    /// putting a missing item back impossible: the one operation whose entire job is to recreate
    /// it was the one that could not.
    ///
    /// Falling back to the record id in the title covers the other direction: an item recreated by
    /// hand has an id the note has never seen, and creating a second one would leave two entries
    /// claiming the same record.
    /// </summary>
    private static string? ResolveExistingItem(List<VaultItemSummary> items, VaultSecretItem item)
    {
        if (item.Spec.ItemId is { } indexed && items.Any(summary => summary.ItemId == indexed))
        {
            return indexed;
        }

        return FindByRecord(items, item.Role, item.RecordId);
    }

    private static string? FindByRecord(List<VaultItemSummary> items, VaultItemRole role, Guid recordId) =>
        items.FirstOrDefault(i =>
            VaultItemNaming.TryParse(i.Title, out var itemRole, out var id)
            && itemRole == role && id == recordId)?.ItemId;

    private static List<OnePasswordVault> ParseVaults(string vaultListJson)
    {
        var vaults = new List<OnePasswordVault>();

        foreach (var node in JsonNode.Parse(vaultListJson) as JsonArray ?? [])
        {
            if (ReadString(node, "name") is { } name && ReadString(node, "id") is { } id)
            {
                vaults.Add(new OnePasswordVault(name, id));
            }
        }

        return vaults;
    }

    /// <summary>
    /// Every vault holding a RavensPort configuration, when the expected one is not there. Reads
    /// item titles only — no item contents — because all that is being asked is which vault this
    /// app was last pointed at, and the rest of the user's vaults are none of its business.
    ///
    /// All of them rather than the first: two configured vaults is a user keeping separate
    /// profiles, and picking one at random would open one and overwrite the other on the next save.
    /// </summary>
    private async Task<List<OnePasswordVault>> FindConfiguredVaultsAsync(
        List<OnePasswordVault> vaults, CancellationToken ct)
    {
        var matchingVaults = vaults.Where(v => VaultProfile.Matches(v.Name)).ToList();
        var matchingChecks = matchingVaults.Select(async vault =>
        {
            try
            {
                var items = await ListItemsAsync(vault.VaultId, vault.Name, ct);
                return items.Any(i => i.Title == VaultItemNaming.ConfigTitle) ? vault : null;
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
                return null;
            }
        });

        var matchingResults = (await Task.WhenAll(matchingChecks)).Where(v => v is not null).Select(v => v!).ToList();
        if (matchingResults.Count > 0) return matchingResults;

        var remainingVaults = vaults.Except(matchingVaults).ToList();
        var remainingChecks = remainingVaults.Select(async vault =>
        {
            try
            {
                var items = await ListItemsAsync(vault.VaultId, vault.Name, ct);
                return items.Any(i => i.Title == VaultItemNaming.ConfigTitle) ? vault : null;
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
                return null;
            }
        });

        return (await Task.WhenAll(remainingChecks)).Where(v => v is not null).Select(v => v!).ToList();
    }

    /// <summary>
    /// The vaults the setup page may offer: named after RavensPort, and either empty or
    /// already RavensPort's. Only the name-matching ones are looked inside — every other vault in
    /// the account is none of this app's business, and listing them all is both the slow answer
    /// and the one that makes a user wonder what else it is reading.
    /// </summary>
    /// <param name="configured">
    /// Vaults the discovery pass has already been through, so their items are not listed twice.
    /// Holding a configuration is precisely what makes a vault adoptable.
    /// </param>
    private async Task<List<string>> FindAdoptableVaultsAsync(
        List<OnePasswordVault> vaults, List<OnePasswordVault> configured, CancellationToken ct)
    {
        var adoptable = new List<string>();

        foreach (var vault in vaults.Where(v => VaultProfile.Matches(v.Name)))
        {
            if (configured.Any(c => c.VaultId == vault.VaultId))
            {
                adoptable.Add(vault.Name);
                continue;
            }

            try
            {
                var items = await ListItemsAsync(vault.VaultId, vault.Name, ct);
                if (VaultAdoption.LooksAdoptable([.. items.Select(i => i.Title)])) adoptable.Add(vault.Name);
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                // A vault this session cannot list cannot be offered either — it would be refused
                // for the same reason the moment it was picked. The rest still get an answer.
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
            }
        }

        return adoptable;
    }

    /// <summary>One vault from <c>vault list</c>.</summary>
    private sealed record OnePasswordVault(string Name, string VaultId);

    private static string? ReadString(JsonNode? node, string property)
    {
        try
        {
            return node?[property]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // The property exists but is not a string. Treating that as absent is right: these
            // shapes come from another program's output, not from a contract this app controls.
            return null;
        }
    }

    /// <summary>
    /// Locates the binary for a call that does not go through a probe first — creating or taking
    /// over a vault, both of which can be the first thing this provider is asked to do.
    /// </summary>
    private void RequireExe()
    {
        _exePath ??= exePathOverride ?? VaultProbe.FindOnePassword();

        if (_exePath is null || !File.Exists(_exePath))
        {
            throw new VaultCliException("The 1Password CLI is not installed.");
        }
    }

    private async Task RequireVaultAsync(CancellationToken ct)
    {
        if (_vaultId is not null) return;

        var status = await ProbeAsync(ct);

        _vaultId = status.VaultId ?? throw (status.Availability switch
        {
            VaultAvailability.NotSignedIn => new VaultLockedException(Kind, status.Detail),
            VaultAvailability.NotInstalled => new VaultCliException("The 1Password CLI is not installed."),
            VaultAvailability.VaultMissing =>
                new VaultCliException($"The '{_vaultName}' vault does not exist yet."),
            VaultAvailability.VaultChoiceNeeded =>
                new VaultCliException("More than one 1Password vault holds a RavensPort configuration. "
                                      + "Choose which one to use on the setup page."),
            _ => (Exception)new VaultCliException(status.Detail ?? "1Password is unavailable."),
        });
    }

    private Task<CliResult> RunAsync(
        IReadOnlyList<string> args, string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var env = ServiceAccountToken is { Length: > 0 } token
            ? new Dictionary<string, string> { ["OP_SERVICE_ACCOUNT_TOKEN"] = token }
            : null;

        return cliRunner.RunAsync(
            _exePath ?? throw new VaultCliException("The 1Password CLI has not been located yet."),
            args, stdin, env, timeout, ct);
    }
}
