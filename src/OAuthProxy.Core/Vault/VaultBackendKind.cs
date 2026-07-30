namespace OAuthProxy.Core.Vault;

/// <summary>
/// Which password manager backs the store. Exactly one is active at a time — writing to both
/// would give two vaults that silently diverge with no way to say which is right.
/// </summary>
public enum VaultBackendKind
{
    /// <summary>Not a real backend: the in-memory double used by tests and by the app before a
    /// backend has been chosen.</summary>
    None = 0,

    OnePassword,
    ProtonPass,
}
