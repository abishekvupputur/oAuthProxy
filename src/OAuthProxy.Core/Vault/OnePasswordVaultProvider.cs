using System.Text.Json;
using System.Text.Json.Nodes;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Vault;

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

    public VaultBackendKind Kind => VaultBackendKind.OnePassword;

    public string VaultName => _vaultName;

    public string? LastLoadWarning { get; private set; }

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

        if (_vaultId is null && await FindConfiguredVaultAsync(vaults, ct) is { } adopted)
        {
            // A vault the user pointed OAuthProxy at is not remembered on this PC — nothing about
            // this app is — so it is found the same way the backend itself is: whichever vault
            // actually holds the configuration is the one that was being used. Only reached when
            // threeEyedRaven is absent, so the ordinary path is still one `vault list`.
            _vaultName = adopted.Name;
            _vaultId = adopted.VaultId;

            activityLog.Log($"VAULT 1Password — using the existing '{_vaultName}' vault, "
                            + "which holds the OAuthProxy configuration");
        }

        return new VaultStatus(
            Kind,
            _vaultId is null ? VaultAvailability.VaultMissing : VaultAvailability.Ready,
            _exePath,
            version?.ToString(),
            _vaultId,
            VaultName: _vaultName);
    }

    public async Task EnsureVaultAsync(CancellationToken ct = default)
    {
        if (_vaultId is not null) return;

        var result = await RunAsync(
            ["vault", "create", _vaultName, "--description", VaultConstants.VaultDescription,
             "--format", "json"],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"Could not create the '{_vaultName}' vault: {result.FirstErrorLine()}",
                partiallyApplied: false);
        }

        _vaultId = ReadString(JsonNode.Parse(result.StdOut), "id")
                   ?? throw new VaultSaveException(
                       "1Password created the vault but did not report its id.", partiallyApplied: false);
    }

    /// <summary>
    /// Takes over a vault the user already has. See <see cref="VaultAdoption"/> for why only an
    /// empty vault or one OAuthProxy has written to is accepted.
    /// </summary>
    public async Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default)
    {
        var name = vaultName.Trim();
        if (name.Length == 0) throw VaultAdoption.NameRequired();

        _exePath ??= exePathOverride ?? VaultProbe.FindOnePassword();
        if (_exePath is null || !File.Exists(_exePath))
        {
            throw new VaultCliException("The 1Password CLI is not installed.");
        }

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

        var outcome = VaultAdoption.Judge(match.Name, items.Count, note);

        _vaultName = match.Name;
        _vaultId = match.VaultId;
        _loadedRevision = 0;
        LastLoadWarning = null;

        if (outcome == VaultAdoptionOutcome.Empty)
        {
            // Stamped now rather than on the first real edit: the config item is the only thing
            // that identifies this vault as OAuthProxy's next launch, and the name the user just
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
        LastLoadWarning = null;
    }

    public async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
    {
        LastLoadWarning = null;

        await RequireVaultAsync(ct);

        var items = await ListOwnedItemsAsync(ct);

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
                              + "so OAuthProxy started with nothing. The item has not been changed.";
            _loadedRevision = 0;
            return new ConfigStore();
        }

        if (document.IsFromANewerLayout)
        {
            LastLoadWarning = $"The vault was written by a newer version of OAuthProxy "
                              + $"(layout {document.VaultLayoutVersion}). Some settings may not be understood.";
        }

        _loadedRevision = document.Revision;

        var secrets = await ResolveSecretsAsync(document.Index, items, ct);
        var warnings = new List<string>();
        var store = VaultMapper.ComposeStore(document, secrets, warnings);

        if (warnings.Count > 0)
        {
            LastLoadWarning = string.Join(" ", warnings.Prepend(LastLoadWarning).Where(w => !string.IsNullOrEmpty(w)));
        }

        return store;
    }

    public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
    {
        await RequireVaultAsync(ct);

        var items = await ListOwnedItemsAsync(ct);
        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);

        var index = await ReadIndexAsync(noteSummary, items, ct);

        var written = 0;
        var secretItems = VaultMapper.BuildSecretItems(store, index);

        foreach (var item in secretItems)
        {
            ct.ThrowIfCancellationRequested();

            // Resolve against the live listing as well as the index: an item recreated by hand in
            // 1Password has a new id the note has never heard of, and creating a second one would
            // leave two entries claiming the same record.
            var existingId = item.Spec.ItemId ?? FindByRecord(items, item.Role, item.RecordId);

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
    private async Task<List<VaultItemSummary>> ListOwnedItemsAsync(CancellationToken ct)
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

    private static string CategoryName(VaultItemCategory category) => category switch
    {
        VaultItemCategory.SecureNote => "Secure Note",
        VaultItemCategory.Login => "Login",
        VaultItemCategory.Password => "Password",
        _ => "Secure Note",
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

        foreach (var ((role, id), itemId) in wanted)
        {
            ct.ThrowIfCancellationRequested();

            if (await GetItemAsync(itemId, ct) is { } contents) resolved[(role, id)] = contents;
        }

        return resolved;
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
    /// The vault holding an OAuthProxy configuration, when the expected one is not there. Reads
    /// item titles only — no item contents — because all that is being asked is which vault this
    /// app was last pointed at, and the rest of the user's vaults are none of its business.
    /// </summary>
    private async Task<OnePasswordVault?> FindConfiguredVaultAsync(
        List<OnePasswordVault> vaults, CancellationToken ct)
    {
        foreach (var vault in vaults)
        {
            try
            {
                var items = await ListItemsAsync(vault.VaultId, vault.Name, ct);
                if (items.Any(i => i.Title == VaultItemNaming.ConfigTitle)) return vault;
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                // A vault this session cannot list is simply not a candidate — one shared with the
                // account but not readable, say. Probing must still reach an answer for the others.
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
            }
        }

        return null;
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
