namespace RavensPort.Core.Vault;

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

    /// <summary>
    /// Memory, and nothing else, for the length of one session — the "try it without handing over
    /// your password manager" mode.
    ///
    /// A backend rather than a special case of <see cref="None"/> on purpose. None means "the gate
    /// has not settled and there is nowhere to write", which is what stops the sync queue and the
    /// tabs; this one is a chosen, working store and everything downstream should treat it as one.
    /// The difference the user sees is that it is never written anywhere and does not survive a
    /// disconnect or a restart.
    /// </summary>
    SingleUse,
}
