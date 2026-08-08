using System.Text.Json.Nodes;

namespace RavensPort.Core.Vault;

public interface IOnePasswordNativeClient
{
    /// <summary>Connects through the 1Password desktop app, which must be running and unlocked.</summary>
    void Initialize(string accountName);

    /// <summary>
    /// Connects as a service account instead. Needs no desktop app: the SDK routes a token to its
    /// own embedded core and talks to 1Password over the network, so nothing local is involved —
    /// which is also why this path cannot hit the integration-channel fault the other one can.
    /// </summary>
    void InitializeServiceAccount(string token);

    JsonNode? ListVaults();
    JsonNode? CreateVault(string name, string description);
    JsonNode? ListItems(string vaultId);
    JsonNode? GetItem(string vaultId, string itemId);
    JsonNode? CreateItem(string vaultId, string itemJson);
    JsonNode? EditItem(string vaultId, string itemId, string itemJson);
    void DeleteItem(string vaultId, string itemId);
}
