using System.Text.Json.Nodes;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// A stand-in for the <c>pass-cli</c> binary, answering in the shapes a real pass-cli 2.2.3
/// produces — including the two asymmetries that are easy to get wrong: a create template writes
/// <c>field_name</c>/<c>field_type</c>/<c>value</c> while a read returns <c>name</c> plus a
/// <c>{"Text"|"Hidden": value}</c> wrapper, and <c>--show-secrets</c> moves the title from the top
/// level down into <c>content.title</c>.
///
/// Deliberately create-only, like the real thing as this app uses it: there is no update path,
/// because <c>item update</c> would put secrets in argv.
/// </summary>
public sealed class FakeProtonPass
{
    /// <summary>Share id to that vault's items, keyed by item id.</summary>
    private readonly Dictionary<string, Dictionary<string, JsonObject>> _byShare = [];

    /// <summary>Vault name to share id, in the order <c>vault list</c> reports them.</summary>
    private readonly List<(string Name, string ShareId)> _vaults = [];

    private int _nextId = 1;

    public string ShareId { get; } = "share-3er";

    public bool VaultExists { get; set; } = true;

    public string Version { get; set; } = "2.2.3";

    public string? WriteFailure { get; set; }

    /// <summary>
    /// Makes reads return Proton Pass's masking placeholder instead of the value, so the
    /// provider's refusal to treat it as a secret can be tested.
    /// </summary>
    public bool MaskSecrets { get; set; }

    /// <summary>Items in the threeEyedRaven vault — what most tests mean by "the vault".</summary>
    public IReadOnlyCollection<JsonObject> Items => ItemsIn(ShareId).Values;

    /// <summary>
    /// Plants a second item claiming a record that already has one — what a failed delete used to
    /// leave behind, and what any save must converge away from.
    /// </summary>
    public void ForgeDuplicate(string title, string password)
    {
        var id = $"-forged{_nextId++}_x";

        ItemsIn(ShareId)[id] = new JsonObject
        {
            ["title"] = title,
            ["username"] = "",
            ["password"] = password,
            ["__type"] = "login",
        };
    }

    /// <summary>
    /// Another vault the account can see, for the "use a vault I already have" path. Returns its
    /// share id, which is what every item call takes.
    /// </summary>
    public string AddVault(string name)
    {
        var shareId = $"share-{name.ToLowerInvariant()}";
        _vaults.Add((name, shareId));

        return shareId;
    }

    /// <summary>
    /// Puts one of the user's own entries in a vault, so it is not empty.
    /// </summary>
    /// <param name="state">
    /// "Trashed" for an item the user has deleted. Proton Pass keeps returning those from
    /// <c>item list</c>, which is why every reader has to look at this field.
    /// </param>
    public string AddItem(string shareId, string title, string password = "hunter2", string state = "Active")
    {
        var id = $"-item{_nextId++}_x";

        ItemsIn(shareId)[id] = new JsonObject
        {
            ["title"] = title,
            ["username"] = "",
            ["password"] = password,
            ["__type"] = "login",
            ["__state"] = state,
        };

        return id;
    }

    /// <summary>The id of the first item in threeEyedRaven whose title matches.</summary>
    public string? ItemIdOf(Func<string, bool> titleMatches) =>
        ItemsIn(ShareId).FirstOrDefault(entry =>
            titleMatches(entry.Value["title"]?.GetValue<string>() ?? "")).Key;

    /// <summary>Moves an item to the trash, the way deleting it in the Proton Pass UI does.</summary>
    public void Trash(string itemId)
    {
        foreach (var items in _byShare.Values)
        {
            if (items.TryGetValue(itemId, out var item)) item["__state"] = "Trashed";
        }
    }

    public IReadOnlyCollection<JsonObject> ItemsInVault(string shareId) => ItemsIn(shareId).Values;

    private Dictionary<string, JsonObject> ItemsIn(string shareId)
    {
        if (!_byShare.TryGetValue(shareId, out var items))
        {
            items = [];
            _byShare[shareId] = items;
        }

        return items;
    }

    public FakeCliRunner AsRunner()
    {
        var runner = new FakeCliRunner();

        runner.Respond(args => args switch
        {
            ["--version"] => Ok(Version),
            ["vault", "list", ..] => Ok(VaultListJson()),
            ["vault", "create", ..] => CreateVault(ValueOf(args, "--name")),
            ["item", "list", ..] => Ok(ItemListJson(ShareOf(args), HasFlag(args, "--show-secrets"))),
            ["item", "create", var type, ..] => CreateItem(runner, type, ShareOf(args)),
            ["item", "delete", ..] => DeleteItem(args),
            _ => null,
        });

        return runner;
    }

    private static CliResult Ok(string stdout) => new(0, stdout, "");

    private static CliResult Fail(string stderr) => new(1, "", stderr);

    /// <summary>
    /// Reads a flag's value the way clap does — and refuses the detached form when the value looks
    /// like a flag, which is exactly how the real CLI behaves.
    ///
    /// This is not pedantry. Proton Pass ids are base64url, so about one in sixty starts with a
    /// hyphen, and `--item-id -0_TRk...` is rejected with "unexpected argument '-0' found". A fake
    /// that quietly accepted it would let a delete-can-never-succeed bug pass every test while
    /// orphaning an item on every save against the real thing.
    /// </summary>
    private static string? ValueOf(IReadOnlyList<string> args, string flag)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(flag + "=", StringComparison.Ordinal)) return arg[(flag.Length + 1)..];
        }

        var index = args.ToList().IndexOf(flag);
        if (index < 0 || index + 1 >= args.Count) return null;

        var value = args[index + 1];
        return value.StartsWith('-') ? null : value;
    }

    /// <summary>True when a flag was passed at all, in either form.</summary>
    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(a => a == flag || a.StartsWith(flag + "=", StringComparison.Ordinal));

    /// <summary>The vault a call names, defaulting to threeEyedRaven for calls that name none.</summary>
    private string ShareOf(IReadOnlyList<string> args) => ValueOf(args, "--share-id") ?? ShareId;

    /// <summary>
    /// Creates the vault the caller named, which is the whole point of naming it. threeEyedRaven is
    /// only the default, and modelling it as the only vault that can be created would let a bug
    /// that ignores the name pass every test.
    /// </summary>
    private CliResult CreateVault(string? name)
    {
        if (name is null || name == VaultConstants.VaultName)
        {
            VaultExists = true;
            return Ok("");
        }

        AddVault(name);
        return Ok("");
    }

    private string VaultListJson()
    {
        var vaults = new JsonArray
        {
            new JsonObject { ["name"] = "Personal", ["vault_id"] = "v-personal", ["share_id"] = "share-personal" },
        };

        if (VaultExists)
        {
            vaults.Add(new JsonObject
            {
                ["name"] = VaultConstants.VaultName,
                ["vault_id"] = "v-3er",
                ["share_id"] = ShareId,
            });
        }

        foreach (var (name, shareId) in _vaults)
        {
            vaults.Add(new JsonObject
            {
                ["name"] = name,
                ["vault_id"] = $"v-{name}",
                ["share_id"] = shareId,
            });
        }

        return new JsonObject { ["vaults"] = vaults }.ToJsonString();
    }

    private string ItemListJson(string shareId, bool withSecrets)
    {
        var array = new JsonArray();

        foreach (var (id, template) in ItemsIn(shareId))
        {
            var title = template["title"]?.GetValue<string>() ?? "";

            if (!withSecrets)
            {
                // Without secrets the title sits at the top level and there is no content at all.
                array.Add(new JsonObject
                {
                    ["id"] = id,
                    ["share_id"] = shareId,
                    ["state"] = StateOf(template),
                    ["title"] = title,
                    ["item_type"] = template["__type"]?.GetValue<string>(),
                });

                continue;
            }

            array.Add(new JsonObject
            {
                ["id"] = id,
                ["share_id"] = shareId,
                // Real pass-cli 2.2.3 reports state at the top level in both modes — checked
                // against the binary, because filtering the wrong shape would hide every item.
                ["state"] = StateOf(template),
                ["content"] = BuildContent(template, title),
            });
        }

        return new JsonObject { ["items"] = array }.ToJsonString();
    }

    private static string StateOf(JsonObject template) =>
        template["__state"]?.GetValue<string>() ?? "Active";

    private JsonObject BuildContent(JsonObject template, string title)
    {
        var type = template["__type"]?.GetValue<string>();

        var content = new JsonObject
        {
            ["title"] = title,
            ["note"] = template["note"]?.GetValue<string>() ?? "",
            ["item_uuid"] = Guid.NewGuid().ToString("D"),
        };

        content["content"] = type switch
        {
            "note" => new JsonObject { ["Note"] = null },
            "login" => new JsonObject
            {
                ["Login"] = new JsonObject
                {
                    ["email"] = "",
                    ["username"] = template["username"]?.GetValue<string>() ?? "",
                    ["password"] = Mask(template["password"]?.GetValue<string>() ?? "", concealed: true),
                    ["urls"] = template["urls"]?.DeepClone() ?? new JsonArray(),
                    ["totp_uri"] = "",
                },
            },
            _ => new JsonObject { ["Custom"] = new JsonObject { ["sections"] = BuildSections(template) } },
        };

        return content;
    }

    private JsonArray BuildSections(JsonObject template)
    {
        var sections = new JsonArray();

        foreach (var section in template["sections"] as JsonArray ?? [])
        {
            var fields = new JsonArray();

            foreach (var field in section?["fields"] as JsonArray ?? [])
            {
                var hidden = field?["field_type"]?.GetValue<string>() == "hidden";
                var value = Mask(field?["value"]?.GetValue<string>() ?? "", hidden);

                fields.Add(new JsonObject
                {
                    ["name"] = field?["field_name"]?.GetValue<string>(),
                    ["content"] = new JsonObject { [hidden ? "Hidden" : "Text"] = value },
                });
            }

            sections.Add(new JsonObject
            {
                ["section_name"] = section?["section_name"]?.GetValue<string>(),
                ["section_fields"] = fields,
            });
        }

        return sections;
    }

    private string Mask(string value, bool concealed) =>
        MaskSecrets && concealed && value.Length > 0 ? "<concealed by Proton Pass>" : value;

    private CliResult CreateItem(FakeCliRunner runner, string type, string shareId)
    {
        if (WriteFailure is { } failure) return Fail(failure);

        if (JsonNode.Parse(runner.Invocations[^1].Stdin ?? "") is not JsonObject template)
        {
            return Fail("Error parsing template JSON. Use --get-template to see the expected format");
        }

        template["__type"] = type;

        // Leading hyphen on purpose: real ids are base64url and roughly one in sixty
        // looks like this, which is what broke deletes passed as two arguments.
        var id = $"-item{_nextId++}_x";
        ItemsIn(shareId)[id] = template;

        // The real CLI prints the bare item id, not JSON.
        return Ok(id + "\n");
    }

    private CliResult DeleteItem(IReadOnlyList<string> args)
    {
        var id = ValueOf(args, "--item-id");

        if (id is null)
        {
            // What clap actually says when a base64url id starting with '-' is passed detached.
            return Fail("error: unexpected argument found");
        }

        return ItemsIn(ShareOf(args)).Remove(id) ? Ok($"Item {id} deleted successfully") : Fail("item not found");
    }
}
