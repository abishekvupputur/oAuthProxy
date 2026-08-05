namespace RavensPort.Core.Vault;

/// <summary>
/// How hard a probe is allowed to try — which is really a question about who gets interrupted.
///
/// The two backends answer "are you signed in?" by asking their CLI, and asking their CLI is
/// exactly what raises a Windows Hello prompt, a 1Password desktop-app approval, or a Proton Pass
/// session unlock. A startup that probed both managers in full therefore opened the app with a
/// stack of authentication prompts nobody had asked for, several of them for a password manager
/// the user was not intending to use at all.
///
/// So the probe is split. <see cref="Discovery"/> answers only what can be answered without
/// credentials, and <see cref="Full"/> — the one that may prompt — runs when the user has pressed
/// a button that says so.
/// </summary>
public enum VaultProbeDepth
{
    /// <summary>
    /// Look, do not knock. The binary is located and asked for its version, and nothing else: no
    /// vault listing, no item listing, no session unlock. A manager that is installed comes back
    /// as <see cref="VaultAvailability.NotConnected"/> rather than signed in or out, because that
    /// question genuinely has not been asked.
    /// </summary>
    Discovery,

    /// <summary>
    /// The real thing — sign-in state, the vault, and the vaults that could be adopted. May raise
    /// an unlock prompt, so it belongs behind a deliberate action.
    /// </summary>
    Full,
}
