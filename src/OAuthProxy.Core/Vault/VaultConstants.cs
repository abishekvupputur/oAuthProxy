namespace OAuthProxy.Core.Vault;

public static class VaultConstants
{
    /// <summary>
    /// The vault this app creates and looks for first, in whichever password manager is active.
    ///
    /// A user can point OAuthProxy at an existing vault instead, and that name is deliberately not
    /// written down anywhere on the PC — nothing about this app is. It is found again the way the
    /// backend itself is: whichever vault actually holds an "OAuthProxy Config" item is the one
    /// that was being used, which is why an adopted empty vault is stamped with that item at once.
    /// </summary>
    public const string VaultName = "threeEyedRaven";

    public const string VaultDescription =
        "OAuthProxy configuration — credentials, tokens, proxy keys, routes, and MCP funnels.";
}
