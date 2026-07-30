namespace OAuthProxy.Core.Vault;

/// <summary>
/// What to tell a user whose password manager keeps locking, and how to sign in in the first place.
///
/// Written carefully on purpose. The obvious advice — lengthen the auto-lock timeout — is advice to
/// weaken a security control: that timeout exists precisely to limit how long an unattended machine
/// holds decrypted secrets. So the options that cost nothing come first, and where the trade-off is
/// real it is stated rather than buried. The app never changes these settings itself; they are the
/// user's to decide.
/// </summary>
public static class VaultLockGuidance
{
    public static string DisplayName(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword => "1Password",
        VaultBackendKind.ProtonPass => "Proton Pass",
        _ => "your password manager",
    };

    public static string InstallCommand(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword => "winget install AgileBits.1Password.CLI",
        VaultBackendKind.ProtonPass => "winget install Proton.PassCLI",
        _ => "",
    };

    public static string DownloadUrl(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword => "https://developer.1password.com/docs/cli/get-started/",
        VaultBackendKind.ProtonPass => "https://protonpass.github.io/pass-cli/",
        _ => "",
    };

    /// <summary>How to get from "installed" to "signed in".</summary>
    public static string SignInSteps(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            "In the 1Password desktop app, open Settings → Developer and turn on "
            + "\"Integrate with 1Password CLI\". Then unlock 1Password.\n\n"
            + "Without the desktop app, run \"op account add\" once and then \"op signin\".",

        VaultBackendKind.ProtonPass =>
            "Run \"pass-cli login\" and complete the sign-in in your browser, or "
            + "\"pass-cli login --interactive\" to stay in the terminal.\n\n"
            + "The session then persists until you run \"pass-cli logout\".",

        _ => "",
    };

    /// <summary>
    /// How to stop the vault locking between saves. Ordered so the option that weakens nothing is
    /// read first.
    /// </summary>
    public static string StayingUnlockedSteps(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            "Best option — a token, so nothing has to stay unlocked:\n"
            + "Create a 1Password service account, grant it access to the "
            + $"\"{VaultConstants.VaultName}\" vault specifically, and put its token in the "
            + "OP_SERVICE_ACCOUNT_TOKEN environment variable. A service account cannot see your "
            + "Private vault, so it reaches only what you gave it.\n\n"
            + "Otherwise — longer unlock window:\n"
            + "1Password → Settings → Security, and raise \"Lock after\". You can also turn off "
            + "\"Lock on sleep\" and \"Lock when the screen saver starts\".\n\n"
            + "That last option is a real trade: the timeout exists to limit how long this machine "
            + "holds your secrets decrypted while you are away from it. Raise it only on a machine "
            + "you would be comfortable leaving unlocked for that long.",

        VaultBackendKind.ProtonPass =>
            "Best option — a token, so nothing has to stay signed in interactively:\n"
            + "Create a Proton Pass personal access token scoped to the "
            + $"\"{VaultConstants.VaultName}\" vault and put it in the "
            + "PROTON_PASS_PERSONAL_ACCESS_TOKEN environment variable.\n\n"
            + "Otherwise:\n"
            + "A pass-cli session lasts until you run \"pass-cli logout\", so a vault that has "
            + "become unavailable usually means the session ended. Run \"pass-cli login\" again.",

        _ => "",
    };

    /// <summary>
    /// Why a 1Password service account still cannot see anything by default. Left out of the
    /// generic text because getting this wrong produces an empty vault list and no clue why.
    /// </summary>
    public static string? TokenCaveat(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            $"A service account must be granted access to \"{VaultConstants.VaultName}\" explicitly — "
            + "it cannot use your built-in Private vault, and without the grant it sees no vaults at all.",

        _ => null,
    };
}
