using System.Text.Json.Nodes;

namespace RavensPort.Core.Vault;

public sealed class OnePasswordNativeClientWrapper : IOnePasswordNativeClient
{
    public void Initialize(string accountName) => OnePasswordNativeClient.Initialize(accountName);
    
    public JsonNode? ListVaults() => OnePasswordNativeClient.ListVaults();
    
    public JsonNode? CreateVault(string name, string description) => OnePasswordNativeClient.CreateVault(name, description);
    
    public JsonNode? ListItems(string vaultId) => OnePasswordNativeClient.ListItems(vaultId);
    
    public JsonNode? GetItem(string vaultId, string itemId) => OnePasswordNativeClient.GetItem(vaultId, itemId);
    
    public JsonNode? CreateItem(string vaultId, string itemJson) => OnePasswordNativeClient.CreateItem(vaultId, itemJson);
    
    public JsonNode? EditItem(string vaultId, string itemId, string itemJson) => OnePasswordNativeClient.EditItem(vaultId, itemId, itemJson);
    
    public void DeleteItem(string vaultId, string itemId) => OnePasswordNativeClient.DeleteItem(vaultId, itemId);
}
