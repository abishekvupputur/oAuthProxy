using System.Text.Json.Nodes;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Vault;

/// <summary>
/// The store, backed by the Proton Pass CLI (<c>pass-cli</c>).
///
/// Shaped by one constraint the 1Password provider does not have: <c>pass-cli item update</c>
/// takes its values as <c>--field NAME=VALUE</c> arguments, and there is no documented way to feed
/// it a secret any other way. A Windows process command line is readable by any process in the
/// session, so using it would publish every client secret and token this app touches.
///
/// <c>pass-cli item create</c> does accept a template on stdin (<c>--from-template -</c>). So this
/// provider never updates: a changed record is written as a new item, the note is rewritten to
/// point at it, and only then is the old item deleted. The ordering is what keeps the invariant —
/// the note never references an item that does not exist. The cost is that Proton Pass item
/// history for these entries is a chain of separate items rather than revisions of one, which is
/// a fair price for not putting secrets in the process table.
///
/// **Unverified surface.** The template JSON shape below is inferred from the documented flags;
/// the CLI's own <c>--get-template</c> is the authority. Every place that depends on it is marked,
/// and a mismatch fails loudly with a message naming that flag rather than silently writing
/// items nothing can read back.
/// </summary>
public sealed class ProtonPassVaultProvider(ICliRunner cliRunner, ActivityLog activityLog) : IConfigVault
{
    /// <summary>
    /// What Proton Pass substitutes for a secret it declines to print. If this comes back instead
    /// of a value, the read has silently failed and treating it as the secret would write the
    /// literal placeholder into the app's config.
    /// </summary>
    private const string ConcealedPlaceholder = "<concealed by Proton Pass>";

    private string? _exePath;
    private string? _shareId;
    private long _loadedRevision;

    public VaultBackendKind Kind => VaultBackendKind.ProtonPass;

    public string? LastLoadWarning { get; private set; }

    /// <summary>
    /// Optional personal access token, for an unattended machine. Passed in the child's
    /// environment, never as an argument.
    /// </summary>
    public string? PersonalAccessToken { get; set; }

    public async Task<VaultStatus> ProbeAsync(CancellationToken ct = default)
    {
        _exePath = VaultProbe.FindProtonPass();
        if (_exePath is null) return VaultStatus.NotInstalled(Kind);

        string? version;
        CliResult vaultList;

        try
        {
            var versionResult = await RunAsync(["--version"], ct: ct);
            version = versionResult.Succeeded ? VaultProbe.ParseVersion(versionResult.StdOut)?.ToString() : null;

            vaultList = await RunAsync(["vault", "list", "--output", "json"], ct: ct);
        }
        catch (VaultCliException ex)
        {
            return VaultStatus.Faulted(Kind, ex.Message, _exePath);
        }

        if (!vaultList.Succeeded)
        {
            // A pass-cli session persists until logout, so this is usually "signed out" rather
            // than "locked". Either way the answer for the user is the same, and the CLI's own
            // wording says which more accurately than an exit code could.
            return new VaultStatus(Kind, VaultAvailability.NotSignedIn,
                _exePath, version, Detail: vaultList.FirstErrorLine());
        }

        _shareId = FindShareId(vaultList.StdOut);

        return new VaultStatus(
            Kind,
            _shareId is null ? VaultAvailability.VaultMissing : VaultAvailability.Ready,
            _exePath,
            version,
            _shareId);
    }

    public async Task EnsureVaultAsync(CancellationToken ct = default)
    {
        if (_shareId is not null) return;

        var result = await RunAsync(
            ["vault", "create", "--name", VaultConstants.VaultName],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"Could not create the '{VaultConstants.VaultName}' vault: {result.FirstErrorLine()}",
                partiallyApplied: false);
        }

        // `vault create` output is not reliably a share id, so re-probe rather than parse it.
        var listed = await RunAsync(["vault", "list", "--output", "json"], ct: ct);
        _shareId = listed.Succeeded ? FindShareId(listed.StdOut) : null;

        if (_shareId is null)
        {
            throw new VaultSaveException(
                $"Proton Pass reported creating '{VaultConstants.VaultName}' but it is not in the vault list.",
                partiallyApplied: false);
        }
    }

    public async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
    {
        LastLoadWarning = null;

        await RequireVaultAsync(ct);

        var items = await ListOwnedItemsAsync(ct);

        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);
        if (noteSummary is null)
        {
            _loadedRevision = 0;
            return new ConfigStore();
        }

        var noteItem = await GetItemAsync(noteSummary.ItemId, ct);
        var document = VaultDocument.TryParse(noteItem?.Field(VaultFields.NoteContent) ?? "");

        if (document is null)
        {
            LastLoadWarning = $"The '{VaultItemNaming.ConfigTitle}' item could not be read as configuration, "
                              + "so OAuthProxy started with nothing. The item has not been changed.";
            _loadedRevision = 0;
            return new ConfigStore();
        }

        if (document.IsFromANewerLayout)
        {
            LastLoadWarning = "The vault was written by a newer version of OAuthProxy "
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

        var previousIndex = await ReadIndexAsync(noteSummary, ct);
        var index = new VaultIndex();

        var written = 0;
        var secretItems = VaultMapper.BuildSecretItems(store, previousIndex);

        foreach (var item in secretItems)
        {
            ct.ThrowIfCancellationRequested();

            var existingId = item.Spec.ItemId ?? FindByRecord(items, item.Role, item.RecordId);

            // Unchanged items are left exactly as they are. Without this every save would rewrite
            // every secret, which on this backend means deleting and recreating them — turning a
            // port change into a full churn of the user's vault.
            if (existingId is not null && await IsUnchangedAsync(existingId, item, ct))
            {
                index.For(item.Role)[item.RecordId] = existingId;
                continue;
            }

            try
            {
                index.For(item.Role)[item.RecordId] = await CreateItemAsync(item.Spec, ct);
                written++;
            }
            catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
            {
                throw new VaultSaveException(
                    $"Could not save '{item.Spec.Title}' to Proton Pass: {ex.Message}",
                    partiallyApplied: written > 0,
                    ex);
            }
        }

        // The note goes after every secret item and before any deletion, so at no point does it
        // reference something that is gone. A crash before it leaves unreferenced new items; a
        // crash after leaves superseded old ones. Both are swept by the next save.
        try
        {
            var note = VaultMapper.BuildConfigNote(store, index, _loadedRevision + 1);
            var newNoteId = await CreateItemAsync(note, ct);
            _loadedRevision++;

            if (noteSummary is not null) await DeleteItemAsync(noteSummary.ItemId, noteSummary.Title, ct);

            activityLog.Log($"VAULT configuration saved to Proton Pass (item {newNoteId})");
        }
        catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
        {
            throw new VaultSaveException(
                $"Could not save the configuration item to Proton Pass: {ex.Message}",
                partiallyApplied: written > 0,
                ex);
        }

        await ReconcileAsync(items, index, ct);
    }

    // ---- CLI calls ------------------------------------------------------------------------------

    /// <summary>
    /// Creates an item from a template on stdin — the only documented write path that does not put
    /// the value in an argument.
    /// </summary>
    private async Task<string> CreateItemAsync(VaultItemSpec spec, CancellationToken ct)
    {
        var result = await RunAsync(
            ["item", "create", TypeName(spec.Category), "--share-id", _shareId!, "--from-template", "-",
             "--output", "json"],
            stdin: BuildTemplate(spec).ToJsonString(),
            timeout: CliRunner.WriteTimeout,
            ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"{result.FirstErrorLine()} (if this mentions the template format, compare it with "
                + $"`pass-cli item create {TypeName(spec.Category)} --get-template`)",
                partiallyApplied: false);
        }

        return ReadString(JsonNode.Parse(result.StdOut), "itemId")
               ?? ReadString(JsonNode.Parse(result.StdOut), "id")
               ?? throw new VaultSaveException(
                   "Proton Pass created the item but did not report its id.", partiallyApplied: false);
    }

    private async Task<List<VaultItemSummary>> ListOwnedItemsAsync(CancellationToken ct)
    {
        var result = await RunAsync(["item", "list", "--share-id", _shareId!, "--output", "json"], ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultCliException(
                $"Could not list the '{VaultConstants.VaultName}' vault: {result.FirstErrorLine()}");
        }

        var items = new List<VaultItemSummary>();

        foreach (var node in JsonNode.Parse(result.StdOut) as JsonArray ?? [])
        {
            var id = ReadString(node, "itemId") ?? ReadString(node, "id");
            var title = ReadString(node, "title") ?? ReadString(node, "name");

            // Only items this app owns. The rest of the vault is the user's and is never read,
            // never written, and never a candidate for deletion.
            if (id is not null && title is not null && VaultItemNaming.IsOwned(title))
            {
                items.Add(new VaultItemSummary(id, title));
            }
        }

        return items;
    }

    private async Task<VaultItemContents?> GetItemAsync(string itemId, CancellationToken ct)
    {
        var result = await RunAsync(
            ["item", "view", "--share-id", _shareId!, "--item-id", itemId, "--output", "json"], ct: ct);

        // A miss is normal: listing and fetching are separate calls, and an item can be removed
        // between them by the user or by another machine.
        if (!result.Succeeded) return null;

        if (JsonNode.Parse(result.StdOut) is not JsonObject node) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var concealed = 0;

        void Record(string name, string? value)
        {
            if (value is null) return;

            if (value == ConcealedPlaceholder)
            {
                // Storing the placeholder would put the literal string "<concealed by Proton
                // Pass>" into the app's config as if it were the secret, and every request using
                // it would fail against the upstream with nothing to explain why.
                concealed++;
                return;
            }

            fields[name] = value;
        }

        // Built-in slots first, then anything under a fields/customFields collection.
        Record(VaultFields.Username, ReadString(node, "username"));
        Record(VaultFields.Password, ReadString(node, "password"));
        Record(VaultFields.Website, (node["urls"] as JsonArray)?.FirstOrDefault()?.GetValue<string>());
        Record(VaultFields.NoteContent, ReadString(node, "content") ?? ReadString(node, "note"));

        foreach (var field in (node["fields"] as JsonArray) ?? (node["customFields"] as JsonArray) ?? [])
        {
            if (ReadString(field, "name") is { Length: > 0 } name)
            {
                Record(name, ReadString(field, "value"));
            }
        }

        if (concealed > 0)
        {
            LastLoadWarning = "Proton Pass returned masked values rather than the stored secrets. "
                              + "OAuthProxy cannot use a masked value; check whether this pass-cli "
                              + "version needs a flag to reveal secrets in JSON output.";
        }

        return new VaultItemContents(itemId, ReadString(node, "title") ?? "", fields);
    }

    private async Task DeleteItemAsync(string itemId, string title, CancellationToken ct)
    {
        var result = await RunAsync(
            ["item", "delete", "--share-id", _shareId!, "--item-id", itemId],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            // Deliberately not fatal. The store is already saved; a leftover item is untidy rather
            // than wrong, and the next save tries again. Throwing would report a successful save
            // as a failure and trigger a pointless rollback.
            activityLog.Log($"VAULT could not delete '{title}': {result.FirstErrorLine()}");
        }
    }

    /// <summary>
    /// Deletes every owned item that is not the one the note now points at: records that are gone,
    /// and the superseded predecessors of records that were rewritten.
    /// </summary>
    private async Task ReconcileAsync(List<VaultItemSummary> before, VaultIndex index, CancellationToken ct)
    {
        var keep = index.Credentials.Values
            .Concat(index.RouteKeys.Values)
            .Concat(index.FunnelKeys.Values)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in before)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out _)) continue;

            // The old config note is deleted by SaveAsync itself, once its replacement exists.
            if (role == VaultItemRole.Config || keep.Contains(item.ItemId)) continue;

            await DeleteItemAsync(item.ItemId, item.Title, ct);
        }
    }

    /// <summary>
    /// Whether the stored item already carries exactly what would be written. Compares values
    /// rather than a stored fingerprint, because on this backend a needless rewrite costs the user
    /// a deleted and recreated vault entry — and one extra read is cheaper than that.
    ///
    /// The caption is excluded on both sides. It is decoration written for whoever browses the
    /// vault, and a proxy key's includes its remaining lifetime, so comparing it would make every
    /// expiring key look changed on every save and churn the user's vault for nothing.
    /// </summary>
    private async Task<bool> IsUnchangedAsync(string itemId, VaultSecretItem item, CancellationToken ct)
    {
        var existing = await GetItemAsync(itemId, ct);
        if (existing is null || existing.Title != item.Spec.Title) return false;

        var expected = item.Spec.Fields.Where(f => f.Name != VaultFields.NoteContent).ToList();

        foreach (var field in expected)
        {
            if (existing.Field(field.Name) != field.Value) return false;
        }

        // A field in the vault that is not being written means something was removed — a
        // disconnected credential's token — which is a change that has to be applied.
        var stored = existing.Fields.Keys.Where(name => name != VaultFields.NoteContent);

        return !stored.Except(expected.Select(f => f.Name)).Any();
    }

    // ---- Templates and parsing ------------------------------------------------------------------

    /// <summary>
    /// The item as a create template.
    ///
    /// **This is the shape that needs verifying against <c>pass-cli item create &lt;type&gt;
    /// --get-template</c>.** Everything else in this provider is built on documented flags; this
    /// is inferred. It is one method on purpose, so correcting it is a local change.
    /// </summary>
    private JsonNode BuildTemplate(VaultItemSpec spec)
    {
        var template = new JsonObject
        {
            ["title"] = spec.Title,
            ["type"] = TypeName(spec.Category),
        };

        var custom = new JsonArray();

        foreach (var field in spec.Fields)
        {
            switch (field.Name)
            {
                case VaultFields.Username:
                    template["username"] = field.Value;
                    break;

                case VaultFields.Password:
                    template["password"] = field.Value;
                    break;

                case VaultFields.Website:
                    template["urls"] = new JsonArray { field.Value };
                    break;

                case VaultFields.NoteContent:
                    // A note item carries its body here; a login item uses the same slot for the
                    // free-text note beneath it.
                    template["content"] = field.Value;
                    template["note"] = field.Value;
                    break;

                default:
                    custom.Add(new JsonObject
                    {
                        ["name"] = field.Name,
                        ["value"] = field.Value,
                        ["hidden"] = field.Concealed,
                    });
                    break;
            }
        }

        if (custom.Count > 0) template["fields"] = custom;

        if (spec.Caption is { Length: > 0 } caption && template["note"] is null)
        {
            template["note"] = caption;
        }

        return template;
    }

    private static string TypeName(VaultItemCategory category) => category switch
    {
        VaultItemCategory.SecureNote => "note",
        VaultItemCategory.Login => "login",

        // Proton Pass has no bare-password type; a login with no username is the closest thing,
        // and keeps the value in the slot the UI conceals and offers to copy.
        VaultItemCategory.Password => "login",
        _ => "note",
    };

    private async Task<VaultIndex> ReadIndexAsync(VaultItemSummary? noteSummary, CancellationToken ct)
    {
        if (noteSummary is null) return new VaultIndex();

        var note = await GetItemAsync(noteSummary.ItemId, ct);
        var document = VaultDocument.TryParse(note?.Field(VaultFields.NoteContent) ?? "");

        if (document is null) return new VaultIndex();

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

        // The index is only a cache. Anything it missed is recovered from the guid in the title.
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

    private static string? FindShareId(string vaultListJson)
    {
        foreach (var node in JsonNode.Parse(vaultListJson) as JsonArray ?? [])
        {
            var name = ReadString(node, "name") ?? ReadString(node, "vaultName");

            if (string.Equals(name, VaultConstants.VaultName, StringComparison.Ordinal))
            {
                return ReadString(node, "shareId") ?? ReadString(node, "id");
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
            // The property exists but is not a string. Treating it as absent is right: this shape
            // comes from another program's output, not from a contract this app controls.
            return null;
        }
    }

    private async Task RequireVaultAsync(CancellationToken ct)
    {
        if (_shareId is not null) return;

        var status = await ProbeAsync(ct);

        _shareId = status.VaultId ?? throw (status.Availability switch
        {
            VaultAvailability.NotSignedIn => new VaultLockedException(Kind, status.Detail),
            VaultAvailability.NotInstalled => new VaultCliException("The Proton Pass CLI is not installed."),
            VaultAvailability.VaultMissing =>
                new VaultCliException($"The '{VaultConstants.VaultName}' vault does not exist yet."),
            _ => (Exception)new VaultCliException(status.Detail ?? "Proton Pass is unavailable."),
        });
    }

    private Task<CliResult> RunAsync(
        IReadOnlyList<string> args, string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var env = PersonalAccessToken is { Length: > 0 } token
            ? new Dictionary<string, string> { ["PROTON_PASS_PERSONAL_ACCESS_TOKEN"] = token }
            : null;

        return cliRunner.RunAsync(
            _exePath ?? throw new VaultCliException("The Proton Pass CLI has not been located yet."),
            args, stdin, env, timeout, ct);
    }
}
