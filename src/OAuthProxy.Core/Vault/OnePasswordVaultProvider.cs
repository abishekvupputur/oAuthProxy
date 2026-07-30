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
    private long _loadedRevision;

    public VaultBackendKind Kind => VaultBackendKind.OnePassword;

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

        _vaultId = FindVaultId(vaultList.StdOut);

        return new VaultStatus(
            Kind,
            _vaultId is null ? VaultAvailability.VaultMissing : VaultAvailability.Ready,
            _exePath,
            version?.ToString(),
            _vaultId);
    }

    public async Task EnsureVaultAsync(CancellationToken ct = default)
    {
        if (_vaultId is not null) return;

        var result = await RunAsync(
            ["vault", "create", VaultConstants.VaultName, "--description", VaultConstants.VaultDescription,
             "--format", "json"],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"Could not create the '{VaultConstants.VaultName}' vault: {result.FirstErrorLine()}",
                partiallyApplied: false);
        }

        _vaultId = ReadString(JsonNode.Parse(result.StdOut), "id")
                   ?? throw new VaultSaveException(
                       "1Password created the vault but did not report its id.", partiallyApplied: false);
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

    private async Task<List<VaultItemSummary>> ListOwnedItemsAsync(CancellationToken ct)
    {
        var result = await RunAsync(["item", "list", "--vault", _vaultId!, "--format", "json"], ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultCliException($"Could not list the '{VaultConstants.VaultName}' vault: {result.FirstErrorLine()}");
        }

        var items = new List<VaultItemSummary>();

        foreach (var node in JsonNode.Parse(result.StdOut) as JsonArray ?? [])
        {
            var id = ReadString(node, "id");
            var title = ReadString(node, "title");

            // Only items this app owns. Everything else in the vault is the user's and is never
            // read, never written, and — crucially — never a candidate for deletion.
            if (id is not null && title is not null && VaultItemNaming.IsOwned(title))
            {
                items.Add(new VaultItemSummary(id, title));
            }
        }

        return items;
    }

    private async Task<VaultItemContents?> GetItemAsync(string itemId, CancellationToken ct)
    {
        var result = await RunAsync(["item", "get", itemId, "--vault", _vaultId!, "--format", "json"], ct: ct);

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

        if (document is null) return new VaultIndex();

        // The revision guard. Both managers sync, so another machine may have written since this
        // one loaded — and overwriting it silently is how two installs quietly destroy each
        // other's routes and keys.
        if (document.Revision != _loadedRevision)
        {
            throw new VaultSaveException(
                $"The configuration in '{VaultConstants.VaultName}' was changed elsewhere"
                + (string.IsNullOrEmpty(document.WrittenBy) ? "" : $" (by {document.WrittenBy})")
                + ". Reload from the vault before saving, or your changes will overwrite theirs.",
                partiallyApplied: false);
        }

        return document.Index;
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

    private string? FindVaultId(string vaultListJson)
    {
        foreach (var node in JsonNode.Parse(vaultListJson) as JsonArray ?? [])
        {
            if (string.Equals(ReadString(node, "name"), VaultConstants.VaultName, StringComparison.Ordinal))
            {
                return ReadString(node, "id");
            }
        }

        return null;
    }

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
                new VaultCliException($"The '{VaultConstants.VaultName}' vault does not exist yet."),
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
