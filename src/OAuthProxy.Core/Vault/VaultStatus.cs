namespace OAuthProxy.Core.Vault;

/// <summary>
/// How far a backend is from being usable. Ordered from least to most ready, because the setup
/// page walks the user up this ladder one rung at a time and each rung needs different advice.
/// </summary>
public enum VaultAvailability
{
    /// <summary>The CLI binary could not be found. The user needs to install it.</summary>
    NotInstalled,

    /// <summary>The binary runs but has no usable session — locked, signed out, or the desktop-app
    /// integration is off.</summary>
    NotSignedIn,

    /// <summary>Signed in, but the threeEyedRaven vault does not exist yet.</summary>
    VaultMissing,

    /// <summary>Signed in and the vault exists. The gate opens on this and nothing else.</summary>
    Ready,

    /// <summary>The CLI failed in a way that is none of the above — a version too old, a broken
    /// install, an unparseable response. <see cref="VaultStatus.Detail"/> carries the reason.</summary>
    Faulted,
}

/// <summary>
/// The result of probing one backend. Everything the setup page needs to render a card and
/// everything the gate needs to decide whether to start the proxy.
/// </summary>
/// <param name="Kind">Which backend this describes.</param>
/// <param name="Availability">How far it is from usable.</param>
/// <param name="ExePath">Resolved path to the CLI binary, when one was found.</param>
/// <param name="Version">Version string reported by the CLI, when it ran.</param>
/// <param name="VaultId">Backend-specific id of the threeEyedRaven vault, when it exists. 1Password
/// calls this a vault id and Proton Pass a share id; both are opaque and only meaningful to their
/// own CLI.</param>
/// <param name="Detail">Human-readable reason, shown verbatim on the setup page. Never contains a
/// secret: it is built from the first line of stderr, which the CLIs use for diagnostics only.</param>
/// <param name="VaultName">Name of the vault in use — threeEyedRaven unless the user pointed
/// OAuthProxy at one they already had. Reported so the Settings tab can say which vault the
/// configuration is actually in, which is not guessable once it is not the default.</param>
public sealed record VaultStatus(
    VaultBackendKind Kind,
    VaultAvailability Availability,
    string? ExePath = null,
    string? Version = null,
    string? VaultId = null,
    string? Detail = null,
    string? VaultName = null)
{
    public bool IsReady => Availability == VaultAvailability.Ready;

    /// <summary>
    /// True when the backend is installed and signed in, so the only thing left is creating the
    /// vault — which the app can do on the user's behalf with one button.
    /// </summary>
    public bool CanCreateVault => Availability == VaultAvailability.VaultMissing;

    public static VaultStatus NotInstalled(VaultBackendKind kind) =>
        new(kind, VaultAvailability.NotInstalled);

    public static VaultStatus Faulted(VaultBackendKind kind, string detail, string? exePath = null) =>
        new(kind, VaultAvailability.Faulted, ExePath: exePath, Detail: detail);
}
