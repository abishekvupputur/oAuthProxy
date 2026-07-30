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
    /// <summary>Item id to the template it was created from.</summary>
    private readonly Dictionary<string, JsonObject> _items = [];

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

    public IReadOnlyCollection<JsonObject> Items => _items.Values;

    public FakeCliRunner AsRunner()
    {
        var runner = new FakeCliRunner();

        runner.Respond(args => args switch
        {
            ["--version"] => Ok(Version),
            ["vault", "list", ..] => Ok(VaultListJson()),
            ["vault", "create", ..] => CreateVault(),
            ["item", "list", ..] => Ok(ItemListJson(args.Contains("--show-secrets"))),
            ["item", "create", var type, ..] => CreateItem(runner, type),
            ["item", "delete", ..] => DeleteItem(args),
            _ => null,
        });

        return runner;
    }

    private static CliResult Ok(string stdout) => new(0, stdout, "");

    private static CliResult Fail(string stderr) => new(1, "", stderr);

    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private CliResult CreateVault()
    {
        VaultExists = true;
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

        return new JsonObject { ["vaults"] = vaults }.ToJsonString();
    }

    private string ItemListJson(bool withSecrets)
    {
        var array = new JsonArray();

        foreach (var (id, template) in _items)
        {
            var title = template["title"]?.GetValue<string>() ?? "";

            if (!withSecrets)
            {
                // Without secrets the title sits at the top level and there is no content at all.
                array.Add(new JsonObject
                {
                    ["id"] = id,
                    ["share_id"] = ShareId,
                    ["state"] = "Active",
                    ["title"] = title,
                    ["item_type"] = template["__type"]?.GetValue<string>(),
                });

                continue;
            }

            array.Add(new JsonObject
            {
                ["id"] = id,
                ["share_id"] = ShareId,
                ["state"] = "Active",
                ["content"] = BuildContent(template, title),
            });
        }

        return new JsonObject { ["items"] = array }.ToJsonString();
    }

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

    private CliResult CreateItem(FakeCliRunner runner, string type)
    {
        if (WriteFailure is { } failure) return Fail(failure);

        if (JsonNode.Parse(runner.Invocations[^1].Stdin ?? "") is not JsonObject template)
        {
            return Fail("Error parsing template JSON. Use --get-template to see the expected format");
        }

        template["__type"] = type;

        var id = $"item-{_nextId++}";
        _items[id] = template;

        // The real CLI prints the bare item id, not JSON.
        return Ok(id + "\n");
    }

    private CliResult DeleteItem(IReadOnlyList<string> args)
    {
        var id = ValueAfter(args, "--item-id");

        return id is not null && _items.Remove(id)
            ? Ok($"Item {id} deleted successfully")
            : Fail("item not found");
    }
}
