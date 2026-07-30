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
    /// <summary>Vault id to that vault's items, keyed by item id.</summary>
    private readonly Dictionary<string, Dictionary<string, JsonObject>> _byVault = [];

    /// <summary>Vault name to id, in the order <c>vault list</c> reports them.</summary>
    private readonly List<(string Name, string VaultId)> _vaults = [];

    private int _nextId = 1;

    public string VaultId { get; } = "vault-3er";

    /// <summary>False until <c>vault create</c> is called, so the missing-vault path can be tested.</summary>
    public bool VaultExists { get; set; } = true;

    public string Version { get; set; } = "2.31.0";

    /// <summary>Set to make every write fail, standing in for a vault that locked mid-save.</summary>
    public string? WriteFailure { get; set; }

    /// <summary>Items in the threeEyedRaven vault — what most tests mean by "the vault".</summary>
    public IReadOnlyCollection<JsonObject> Items => ItemsIn(VaultId).Values;

    /// <summary>
    /// Another vault the account can see, for the "use a vault I already have" path. Returns its
    /// id, which is what every item call takes.
    /// </summary>
    public string AddVault(string name)
    {
        var vaultId = $"vault-{name.ToLowerInvariant()}";
        _vaults.Add((name, vaultId));

        return vaultId;
    }

    /// <summary>Puts one of the user's own entries in a vault, so it is not empty.</summary>
    public void AddItem(string vaultId, string title) =>
        ItemsIn(vaultId)[$"item-{_nextId++}"] = new JsonObject
        {
            ["title"] = title,
            ["category"] = "Login",
            ["fields"] = new JsonArray(),
        };

    public IReadOnlyCollection<JsonObject> ItemsInVault(string vaultId) => ItemsIn(vaultId).Values;

    private Dictionary<string, JsonObject> ItemsIn(string vaultId)
    {
        if (!_byVault.TryGetValue(vaultId, out var items))
        {
            items = [];
            _byVault[vaultId] = items;
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
            ["vault", "create", ..] => CreateVault(),
            ["item", "list", ..] => Ok(ItemListJson(VaultOf(args))),
            ["item", "get", var id, ..] => GetItem(id, VaultOf(args)),
            ["item", "create", ..] => CreateItem(runner, VaultOf(args)),
            ["item", "edit", var id, ..] => EditItem(runner, id, VaultOf(args)),
            ["item", "delete", var id, ..] => DeleteItem(id, VaultOf(args)),
            _ => null,
        });

        return runner;
    }

    /// <summary>The vault a call names, defaulting to threeEyedRaven for calls that name none.</summary>
    private string VaultOf(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf("--vault");

        return index >= 0 && index + 1 < args.Count ? args[index + 1] : VaultId;
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

        foreach (var (name, vaultId) in _vaults)
        {
            vaults.Add(new JsonObject { ["id"] = vaultId, ["name"] = name });
        }

        return vaults.ToJsonString();
    }

    private string ItemListJson(string vaultId)
    {
        var array = new JsonArray();

        foreach (var (id, item) in ItemsIn(vaultId))
        {
            array.Add(new JsonObject { ["id"] = id, ["title"] = item["title"]?.GetValue<string>() });
        }

        return array.ToJsonString();
    }

    private CliResult GetItem(string id, string vaultId) =>
        ItemsIn(vaultId).TryGetValue(id, out var item)
            ? Ok(new JsonObject
            {
                ["id"] = id,
                ["title"] = item["title"]?.GetValue<string>(),
                ["fields"] = item["fields"]?.DeepClone(),
            }.ToJsonString())
            : Fail($"\"{id}\" isn't an item.");

    private CliResult CreateItem(FakeCliRunner runner, string vaultId)
    {
        if (WriteFailure is { } failure) return Fail(failure);

        var template = ParseStdin(runner);
        if (template is null) return Fail("expected an item template on stdin");

        var id = $"item-{_nextId++}";
        ItemsIn(vaultId)[id] = template;

        return Ok(new JsonObject { ["id"] = id, ["title"] = template["title"]?.GetValue<string>() }.ToJsonString());
    }

    private CliResult EditItem(FakeCliRunner runner, string id, string vaultId)
    {
        if (WriteFailure is { } failure) return Fail(failure);
        if (!ItemsIn(vaultId).TryGetValue(id, out var existing)) return Fail($"\"{id}\" isn't an item.");

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

    private CliResult DeleteItem(string id, string vaultId) =>
        ItemsIn(vaultId).Remove(id) ? Ok("") : Fail($"\"{id}\" isn't an item.");

    /// <summary>The template the provider piped in for the call being handled.</summary>
    private static JsonObject? ParseStdin(FakeCliRunner runner) =>
        JsonNode.Parse(runner.Invocations[^1].Stdin ?? "") as JsonObject;
}
