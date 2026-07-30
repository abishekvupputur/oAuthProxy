namespace OAuthProxy.Core.Vault;

public static class VaultConstants
{
    /// <summary>
    /// The vault this app reads and writes, in whichever password manager is active. Fixed rather
    /// than configurable: it is the one piece of state that cannot itself be stored in the vault,
    /// and a wrong or forgotten name would look exactly like an empty configuration.
    /// </summary>
    public const string VaultName = "threeEyedRaven";

    public const string VaultDescription =
        "OAuthProxy configuration — credentials, tokens, proxy keys, routes, and MCP funnels.";
}
