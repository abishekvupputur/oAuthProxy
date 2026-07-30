namespace OAuthProxy.Core.Vault;

/// <summary>
/// Whether the vault can currently accept writes. The store is the only place config lives, so
/// "cannot write" is a real mode the whole app has to respect rather than an error to retry past.
/// </summary>
public enum VaultAccess
{
    /// <summary>Normal operation: edits and token refreshes are allowed.</summary>
    Writable,

    /// <summary>
    /// The manager is locked, signed out, or otherwise unreachable. Edits are refused outright and
    /// token refresh is suspended — see <see cref="VaultLockedException"/> for why nothing is
    /// queued for later.
    /// </summary>
    ReadOnly,
}
