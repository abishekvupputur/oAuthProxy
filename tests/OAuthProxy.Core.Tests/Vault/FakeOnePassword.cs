using System.Text.Json.Nodes;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// A stand-in for the <c>op</c> binary: keeps items in a dictionary and answers the subcommands
/// the provider uses, in the JSON shapes 1Password actually emits.
///
/// Worth the effort over per-call canned responses because it makes a real save-then-load round
/// trip possible — which is what exercises template building and response parsing together. A
/// mapper bug that writes a field the parser cannot read back is invisible to any test that
/// scripts both sides independently.
/// </summary>
public sealed class FakeOnePassword
{
    private readonly Dictionary<string, JsonObject> _items = [];

    private int _nextId = 1;

    public string VaultId { get; } = "vault-3er";

    /// <summary>False until <c>vault create</c> is called, so the missing-vault path can be tested.</summary>
    public bool VaultExists { get; set; } = true;

    public string Version { get; set; } = "2.31.0";

    /// <summary>Set to make every write fail, standing in for a vault that locked mid-save.</summary>
    public string? WriteFailure { get; set; }

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
            ["item", "get", var id, ..] => GetItem(id),
            ["item", "create", ..] => CreateItem(runner),
            ["item", "edit", var id, ..] => EditItem(runner, id),
            ["item", "delete", var id, ..] => DeleteItem(id),
            _ => null,
        });

        return runner;
    }

    private static CliResult Ok(string stdout) => new(0, stdout, "");

    private static CliResult Fail(string stderr) => new(1, "", stderr);

    private CliResult CreateVault()
    {
        VaultExists = true;
        return Ok(new JsonObject { ["id"] = VaultId, ["name"] = VaultConstants.VaultName }.ToJsonString());
    }

    private string VaultListJson()
    {
        var vaults = new JsonArray { new JsonObject { ["id"] = "vault-private", ["name"] = "Private" } };

        if (VaultExists)
        {
            vaults.Add(new JsonObject { ["id"] = VaultId, ["name"] = VaultConstants.VaultName });
        }

        return vaults.ToJsonString();
    }

    private string ItemListJson()
    {
        var array = new JsonArray();

        foreach (var (id, item) in _items)
        {
            array.Add(new JsonObject { ["id"] = id, ["title"] = item["title"]?.GetValue<string>() });
        }

        return array.ToJsonString();
    }

    private CliResult GetItem(string id) =>
        _items.TryGetValue(id, out var item)
            ? Ok(new JsonObject
            {
                ["id"] = id,
                ["title"] = item["title"]?.GetValue<string>(),
                ["fields"] = item["fields"]?.DeepClone(),
            }.ToJsonString())
            : Fail($"\"{id}\" isn't an item.");

    private CliResult CreateItem(FakeCliRunner runner)
    {
        if (WriteFailure is { } failure) return Fail(failure);

        var template = ParseStdin(runner);
        if (template is null) return Fail("expected an item template on stdin");

        var id = $"item-{_nextId++}";
        _items[id] = template;

        return Ok(new JsonObject { ["id"] = id, ["title"] = template["title"]?.GetValue<string>() }.ToJsonString());
    }

    private CliResult EditItem(FakeCliRunner runner, string id)
    {
        if (WriteFailure is { } failure) return Fail(failure);
        if (!_items.TryGetValue(id, out var existing)) return Fail($"\"{id}\" isn't an item.");

        var template = ParseStdin(runner);
        if (template is null) return Fail("expected an item template on stdin");

        // 1Password merges an edit rather than replacing the item, which is exactly why the
        // provider has to send empty values for fields that should go away.
        existing["title"] = template["title"]?.DeepClone();

        var merged = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var field in (existing["fields"] as JsonArray ?? []).Concat(template["fields"] as JsonArray ?? []))
        {
            if (field?["id"]?.GetValue<string>() is { } fieldId) merged[fieldId] = field.DeepClone();
        }

        var fields = new JsonArray();
        foreach (var field in merged.Values) fields.Add(field);
        existing["fields"] = fields;

        return Ok(new JsonObject { ["id"] = id }.ToJsonString());
    }

    private CliResult DeleteItem(string id) =>
        _items.Remove(id) ? Ok("") : Fail($"\"{id}\" isn't an item.");

    /// <summary>The template the provider piped in for the call being handled.</summary>
    private static JsonObject? ParseStdin(FakeCliRunner runner) =>
        JsonNode.Parse(runner.Invocations[^1].Stdin ?? "") as JsonObject;
}
