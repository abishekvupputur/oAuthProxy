using System.Text.Json.Nodes;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Vault;

/// <summary>
/// The store, backed by the Proton Pass CLI (<c>pass-cli</c>).
///
/// Shaped by one constraint the 1Password provider does not have: <c>pass-cli item update</c>
/// takes its values as <c>--field name=value</c> arguments and offers no other way in. A Windows
/// process command line is readable by any process in the session, so using it would publish every
/// client secret and token this app touches.
///
/// <c>item create</c> does accept a template on stdin (<c>--from-template -</c>), so this provider
/// never updates. A changed record is written as a new item, the note is rewritten to point at it,
/// and only then is the old item deleted. That ordering keeps the invariant — the note never
/// references an item that is gone. The cost is that Proton Pass history for these entries is a
/// chain of items rather than revisions of one, which is a fair price for keeping secrets off the
/// command line.
///
/// The wire shapes below were taken from a real pass-cli 2.2.3 (<c>--get-template</c> for writes,
/// observed output for reads), not inferred. Two of them are asymmetric and easy to get wrong:
/// a template writes <c>field_name</c>/<c>field_type</c>/<c>value</c> while a read returns
/// <c>name</c> plus a <c>{"Text"|"Hidden": value}</c> wrapper, and <c>--show-secrets</c> moves the
/// title from the top level down into <c>content.title</c>.
/// </summary>
/// <param name="exePathOverride">
/// Skips the search for the binary. For tests, which must not depend on whether the real CLI
/// happens to be installed — and must not reach for the process-wide environment variable, since
/// test classes run in parallel and would clobber each other's.
/// </param>
public sealed class ProtonPassVaultProvider(
    ICliRunner cliRunner,
    ActivityLog activityLog,
    string? exePathOverride = null) : IConfigVault
{
    /// <summary>Section every custom field goes in, so the Proton Pass UI groups them together.</summary>
    private const string SectionName = "OAuthProxy";

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
        _exePath = exePathOverride ?? VaultProbe.FindProtonPass();
        if (_exePath is null || !File.Exists(_exePath)) return VaultStatus.NotInstalled(Kind);

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

        // `vault create` does not report the share id, so re-list rather than parse its output.
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

        // One call for the whole vault, secrets included. `item list --show-secrets` returns full
        // contents, so there is no reason to fetch items one at a time — which on a store with a
        // dozen credentials would be a dozen extra subprocess launches on the startup path.
        var items = await ListAsync(withSecrets: true, ct);

        var note = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);
        if (note is null)
        {
            _loadedRevision = 0;
            return new ConfigStore();
        }

        var document = VaultDocument.TryParse(note.Contents.Field(VaultFields.NoteContent) ?? "");

        if (document is null)
        {
            // The note is free text the user can open and edit in Proton Pass, so a broken one is
            // a mistake rather than corruption. Coming up empty is recoverable; refusing to start
            // is not, and the old configuration is still in the vault to be repaired.
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

        var warnings = new List<string>();
        var store = VaultMapper.ComposeStore(document, ResolveSecrets(document.Index, items), warnings);

        if (warnings.Count > 0)
        {
            LastLoadWarning = string.Join(" ", warnings.Prepend(LastLoadWarning).Where(w => !string.IsNullOrEmpty(w)));
        }

        return store;
    }

    public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
    {
        await RequireVaultAsync(ct);

        var existing = await ListAsync(withSecrets: true, ct);
        var previousNote = existing.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);

        var previousIndex = ReadIndex(previousNote);
        var index = new VaultIndex();

        var written = 0;
        var secretItems = VaultMapper.BuildSecretItems(store, previousIndex);

        foreach (var item in secretItems)
        {
            ct.ThrowIfCancellationRequested();

            var current = FindCurrent(existing, item, previousIndex);

            // Unchanged records are left alone. On this backend a rewrite means deleting and
            // recreating the user's vault entry, so saving a store whose secrets have not moved
            // must not churn it — a port change would otherwise replace every credential item.
            if (current is not null && IsUnchanged(current, item))
            {
                index.For(item.Role)[item.RecordId] = current.ItemId;
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

        // After every secret item and before any deletion, so at no point does the note reference
        // something that is gone. A crash before it leaves unreferenced new items; a crash after
        // leaves superseded old ones. The next save sweeps both.
        try
        {
            var noteSpec = VaultMapper.BuildConfigNote(store, index, _loadedRevision + 1);
            await CreateItemAsync(noteSpec, ct);
            _loadedRevision++;

            if (previousNote is not null) await DeleteItemAsync(previousNote.ItemId, previousNote.Title, ct);
        }
        catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
        {
            throw new VaultSaveException(
                $"Could not save the configuration item to Proton Pass: {ex.Message}",
                partiallyApplied: written > 0,
                ex);
        }

        await ReconcileAsync(existing, index, ct);
    }

    // ---- CLI calls ------------------------------------------------------------------------------

    /// <summary>
    /// Ids as <c>--flag=value</c> rather than two arguments.
    ///
    /// Proton Pass ids are base64url, so roughly one in sixty starts with a hyphen — and
    /// <c>--item-id -0_TRk…</c> is parsed by the CLI as an unknown flag, not as a value:
    /// "error: unexpected argument '-0' found". The attached form has no such ambiguity.
    ///
    /// The symptom was ugly and permanent. A delete that can never succeed leaves an orphaned
    /// item behind on every save, so a route ended up with two key items and no way to tell which
    /// one the proxy was actually accepting.
    /// </summary>
    private string Share => $"--share-id={_shareId}";

    private static string ItemId(string itemId) => $"--item-id={itemId}";


    /// <summary>
    /// Creates an item from a template on stdin — the only write path that keeps the value out of
    /// an argument. Returns the new item id, which the CLI prints bare rather than as JSON.
    /// </summary>
    private async Task<string> CreateItemAsync(VaultItemSpec spec, CancellationToken ct)
    {
        var type = TypeName(spec.Category);

        var result = await RunAsync(
            ["item", "create", type, Share, "--from-template", "-"],
            stdin: BuildTemplate(spec).ToJsonString(),
            timeout: CliRunner.WriteTimeout,
            ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"{result.FirstErrorLine()} (if this mentions the template format, compare it with "
                + $"`pass-cli item create {type} --get-template`)",
                partiallyApplied: false);
        }

        var itemId = result.StdOut.Trim();

        return itemId.Length > 0
            ? itemId
            : throw new VaultSaveException(
                "Proton Pass created the item but did not report its id.", partiallyApplied: false);
    }

    private async Task<List<ProtonItem>> ListAsync(bool withSecrets, CancellationToken ct)
    {
        string[] args = withSecrets
            ? ["item", "list", Share, "--output", "json", "--show-secrets"]
            : ["item", "list", Share, "--output", "json"];

        var result = await RunAsync(args, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultCliException(
                $"Could not list the '{VaultConstants.VaultName}' vault: {result.FirstErrorLine()}");
        }

        var items = new List<ProtonItem>();
        var concealed = 0;

        foreach (var node in JsonNode.Parse(result.StdOut)?["items"] as JsonArray ?? [])
        {
            if (ParseItem(node, ref concealed) is { } item && VaultItemNaming.IsOwned(item.Title))
            {
                // Only items this app owns. The rest of the vault is the user's and is never read,
                // never written, and never a candidate for deletion.
                items.Add(item);
            }
        }

        if (concealed > 0)
        {
            // Storing the placeholder would put the literal string into the app's config as if it
            // were the secret, and every request using it would fail against the upstream with
            // nothing to explain why.
            LastLoadWarning = "Proton Pass returned masked values rather than the stored secrets, "
                              + "so some credentials loaded without them.";
        }

        return items;
    }

    /// <summary>
    /// One item from <c>item list --output json</c>, with or without <c>--show-secrets</c>. The
    /// two shapes differ: without secrets the title is top level and there is no content; with
    /// them the title moves into <c>content.title</c> and the payload hangs off a type-tagged
    /// wrapper.
    /// </summary>
    private static ProtonItem? ParseItem(JsonNode? node, ref int concealed)
    {
        var itemId = ReadString(node, "id");
        if (itemId is null) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        if (node?["content"] is not JsonObject content)
        {
            return ReadString(node, "title") is { } listedTitle
                ? new ProtonItem(itemId, listedTitle, new VaultItemContents(itemId, listedTitle, fields))
                : null;
        }

        var title = ReadString(content, "title") ?? "";

        // The note slot carries the config document for a note item, and a human-readable caption
        // for everything else. Both land here; only the config note is ever read back.
        if (ReadString(content, "note") is { Length: > 0 } note) fields[VaultFields.NoteContent] = note;

        var payload = content["content"];

        if (payload?["Login"] is JsonObject login)
        {
            Record(fields, VaultFields.Username, ReadString(login, "username"), ref concealed);
            Record(fields, VaultFields.Password, ReadString(login, "password"), ref concealed);
            Record(fields, VaultFields.Website, (login["urls"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(),
                ref concealed);
        }
        else if (payload?["Custom"] is JsonObject custom)
        {
            foreach (var section in custom["sections"] as JsonArray ?? [])
            {
                foreach (var field in section?["section_fields"] as JsonArray ?? [])
                {
                    var name = ReadString(field, "name");
                    if (name is null) continue;

                    // Reads wrap the value in its type — {"Text": "..."} or {"Hidden": "..."} —
                    // while writes use a flat field_type/value pair. Neither side is wrong; they
                    // just are not the same shape.
                    var wrapper = field?["content"];
                    Record(fields, name, ReadString(wrapper, "Text") ?? ReadString(wrapper, "Hidden"), ref concealed);
                }
            }
        }

        return new ProtonItem(itemId, title, new VaultItemContents(itemId, title, fields));
    }

    private static void Record(Dictionary<string, string> fields, string name, string? value, ref int concealed)
    {
        if (string.IsNullOrEmpty(value)) return;

        if (value.Contains("concealed by Proton Pass", StringComparison.OrdinalIgnoreCase))
        {
            concealed++;
            return;
        }

        fields[name] = value;
    }

    private async Task DeleteItemAsync(string itemId, string title, CancellationToken ct)
    {
        var result = await RunAsync(
            ["item", "delete", Share, ItemId(itemId)],
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
    /// Deletes every owned item the note no longer points at: records that are gone, and the
    /// superseded predecessors of records that were rewritten.
    /// </summary>
    private async Task ReconcileAsync(List<ProtonItem> before, VaultIndex index, CancellationToken ct)
    {
        var keep = index.Credentials.Values
            .Concat(index.RouteKeys.Values)
            .Concat(index.FunnelKeys.Values)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in before)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out _)) continue;

            // The previous config note is deleted by SaveAsync itself, once its replacement exists.
            if (role == VaultItemRole.Config || keep.Contains(item.ItemId)) continue;

            await DeleteItemAsync(item.ItemId, item.Title, ct);
        }
    }

    // ---- Templates and lookups --------------------------------------------------------------------

    /// <summary>
    /// The item as a create template, in the shape <c>--get-template</c> reports for its type.
    ///
    /// The type choice is forced by what each template can carry. A login has no custom fields at
    /// all, so a credential — which needs a client id, a secret, an API key, two tokens and their
    /// timestamps — has to be a custom item. A proxy key is a single secret, so it stays a login,
    /// where Proton Pass gives it the usual conceal-and-copy treatment.
    /// </summary>
    private JsonNode BuildTemplate(VaultItemSpec spec)
    {
        var template = new JsonObject { ["title"] = spec.Title };

        switch (spec.Category)
        {
            case VaultItemCategory.SecureNote:
                template["note"] = spec.Field(VaultFields.NoteContent) ?? "";
                return template;

            case VaultItemCategory.Password:
                // The login template has no note field, so the caption is dropped here rather
                // than smuggled somewhere it would be read back as data.
                template["username"] = "";
                template["password"] = spec.Field(VaultFields.Password) ?? "";
                return template;

            default:
                var fields = new JsonArray();

                foreach (var field in spec.Fields)
                {
                    fields.Add(new JsonObject
                    {
                        ["field_name"] = field.Name,
                        ["field_type"] = field.Concealed ? "hidden" : "text",
                        ["value"] = field.Value,
                    });
                }

                template["note"] = spec.Caption ?? "";
                template["sections"] = new JsonArray
                {
                    new JsonObject { ["section_name"] = SectionName, ["fields"] = fields },
                };

                return template;
        }
    }

    private static string TypeName(VaultItemCategory category) => category switch
    {
        VaultItemCategory.SecureNote => "note",
        VaultItemCategory.Password => "login",
        _ => "custom",
    };

    /// <summary>
    /// Whether the stored item already carries what would be written.
    ///
    /// Compared against <see cref="WritableFields"/> rather than the whole spec, because not every
    /// template can hold every field: a login has nowhere to put a record id, so a proxy key's is
    /// dropped on the way in and absent on the way out. Comparing it would make every proxy key
    /// look changed on every save and churn the user's vault forever.
    ///
    /// The caption is excluded for the same reason it is written but never read: it is decoration,
    /// and a proxy key's includes its remaining lifetime.
    /// </summary>
    private static bool IsUnchanged(ProtonItem current, VaultSecretItem item)
    {
        if (current.Title != item.Spec.Title) return false;

        var expected = WritableFields(item.Spec)
            .Where(f => f.Value.Length > 0)
            .ToList();

        foreach (var field in expected)
        {
            if (current.Contents.Field(field.Name) != field.Value) return false;
        }

        // A field in the vault that is not being written means something was removed — a
        // disconnected credential's token — which is a change that has to be applied.
        var stored = current.Contents.Fields
            .Where(f => f.Key != VaultFields.NoteContent && f.Value.Length > 0)
            .Select(f => f.Key);

        return !stored.Except(expected.Select(f => f.Name)).Any();
    }

    /// <summary>
    /// The fields <see cref="BuildTemplate"/> will actually store for this item's type. Kept
    /// beside it, because the two drifting apart is what makes an item rewrite itself on every
    /// save with nothing visibly wrong.
    /// </summary>
    private static IEnumerable<VaultItemField> WritableFields(VaultItemSpec spec) => spec.Category switch
    {
        VaultItemCategory.SecureNote => spec.Fields.Where(f => f.Name == VaultFields.NoteContent),
        VaultItemCategory.Password => spec.Fields.Where(f => f.Name == VaultFields.Password),
        _ => spec.Fields.Where(f => f.Name != VaultFields.NoteContent),
    };

    private static ProtonItem? FindCurrent(List<ProtonItem> existing, VaultSecretItem item, VaultIndex index)
    {
        if (index.Find(item.Role, item.RecordId) is { } indexed
            && existing.FirstOrDefault(i => i.ItemId == indexed) is { } byIndex)
        {
            return byIndex;
        }

        // The index is only a cache. An item recreated by hand has an id the note has never seen,
        // and without this fallback the save would add a second item claiming the same record.
        return existing.FirstOrDefault(i =>
            VaultItemNaming.TryParse(i.Title, out var role, out var id)
            && role == item.Role && id == item.RecordId);
    }

    /// <summary>
    /// The index from the note that is about to be replaced.
    ///
    /// Deliberately does not compare revisions. This app assumes a single instance, and the sync
    /// queue re-reads and rewrites whenever it can — so a guard here would turn a lock that lifted
    /// at an awkward moment into a save that refuses and retries forever, which is a worse outcome
    /// than the concurrent write it would be guarding against. The revision is still stamped into
    /// the note, so a second writer is at least visible after the fact.
    /// </summary>
    private static VaultIndex ReadIndex(ProtonItem? note) =>
        note is not null && VaultDocument.TryParse(note.Contents.Field(VaultFields.NoteContent) ?? "") is { } document
            ? document.Index
            : new VaultIndex();

    private static Dictionary<(VaultItemRole, Guid), VaultItemContents> ResolveSecrets(
        VaultIndex index, List<ProtonItem> items)
    {
        var byId = items.ToDictionary(i => i.ItemId, i => i.Contents, StringComparer.Ordinal);
        var resolved = new Dictionary<(VaultItemRole, Guid), VaultItemContents>();

        foreach (var role in new[] { VaultItemRole.Credential, VaultItemRole.RouteKey, VaultItemRole.FunnelKey })
        {
            foreach (var (recordId, itemId) in index.For(role))
            {
                if (byId.TryGetValue(itemId, out var contents)) resolved[(role, recordId)] = contents;
            }
        }

        // Anything the index missed, recovered from the guid in the title.
        foreach (var item in items)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out var id)) continue;
            if (role == VaultItemRole.Config) continue;

            resolved.TryAdd((role, id), item.Contents);
        }

        return resolved;
    }

    private static string? FindShareId(string vaultListJson)
    {
        foreach (var node in JsonNode.Parse(vaultListJson)?["vaults"] as JsonArray ?? [])
        {
            if (string.Equals(ReadString(node, "name"), VaultConstants.VaultName, StringComparison.Ordinal))
            {
                return ReadString(node, "share_id");
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

    /// <summary>An item as this provider needs it: its id, its title, and its fields flattened.</summary>
    private sealed record ProtonItem(string ItemId, string Title, VaultItemContents Contents);
}
