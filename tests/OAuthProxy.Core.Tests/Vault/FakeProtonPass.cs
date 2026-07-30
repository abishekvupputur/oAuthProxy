using System.Text.Json.Nodes;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// A stand-in for the <c>pass-cli</c> binary. Answers the subcommands the provider uses and keeps
/// items in a dictionary, so a save-then-load round trip exercises template building and response
/// parsing together.
///
/// Deliberately create-only, like the real thing as this app uses it: there is no update path,
/// because <c>item update</c> would put secrets in argv.
/// </summary>
public sealed class FakeProtonPass
{
    private readonly Dictionary<string, JsonObject> _items = [];

    private int _nextId = 1;

    public string ShareId { get; } = "share-3er";

    public bool VaultExists { get; set; } = true;

    public string Version { get; set; } = "1.4.0";

    public string? WriteFailure { get; set; }

    /// <summary>
    /// Makes reads return Proton Pass's masking placeholder instead of the value, so the provider's
    /// refusal to treat it as a secret can be tested.
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
            ["item", "list", ..] => Ok(ItemListJson()),
            ["item", "view", ..] => ViewItem(args),
            ["item", "create", ..] => CreateItem(runner),
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
        var vaults = new JsonArray { new JsonObject { ["shareId"] = "share-personal", ["name"] = "Personal" } };

        if (VaultExists)
        {
            vaults.Add(new JsonObject { ["shareId"] = ShareId, ["name"] = VaultConstants.VaultName });
        }

        return vaults.ToJsonString();
    }

    private string ItemListJson()
    {
        var array = new JsonArray();

        foreach (var (id, item) in _items)
        {
            array.Add(new JsonObject { ["itemId"] = id, ["title"] = item["title"]?.GetValue<string>() });
        }

        return array.ToJsonString();
    }

    private CliResult ViewItem(IReadOnlyList<string> args)
    {
        var id = ValueAfter(args, "--item-id");

        if (id is null || !_items.TryGetValue(id, out var item)) return Fail("item not found");

        var view = item.DeepClone().AsObject();

        if (MaskSecrets)
        {
            if (view["password"] is not null) view["password"] = "<concealed by Proton Pass>";

            foreach (var field in view["fields"] as JsonArray ?? [])
            {
                if (field?["hidden"]?.GetValue<bool>() == true) field["value"] = "<concealed by Proton Pass>";
            }
        }

        view["itemId"] = id;
        return Ok(view.ToJsonString());
    }

    private CliResult CreateItem(FakeCliRunner runner)
    {
        if (WriteFailure is { } failure) return Fail(failure);

        if (JsonNode.Parse(runner.Invocations[^1].Stdin ?? "") is not JsonObject template)
        {
            return Fail("expected a template on stdin");
        }

        var id = $"item-{_nextId++}";
        _items[id] = template;

        return Ok(new JsonObject { ["itemId"] = id }.ToJsonString());
    }

    private CliResult DeleteItem(IReadOnlyList<string> args)
    {
        var id = ValueAfter(args, "--item-id");
        return id is not null && _items.Remove(id) ? Ok("") : Fail("item not found");
    }
}
