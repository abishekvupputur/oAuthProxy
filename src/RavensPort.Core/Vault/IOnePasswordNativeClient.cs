using System.Text.Json.Nodes;

namespace RavensPort.Core.Vault;

public interface IOnePasswordNativeClient
{
    void Initialize(string accountName);
    JsonNode? ListVaults();
    JsonNode? CreateVault(string name, string description);
    JsonNode? ListItems(string vaultId);
    JsonNode? GetItem(string vaultId, string itemId);
    JsonNode? CreateItem(string vaultId, string itemJson);
    JsonNode? EditItem(string vaultId, string itemId, string itemJson);
    void DeleteItem(string vaultId, string itemId);
}
