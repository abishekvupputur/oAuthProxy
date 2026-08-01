using System.Text.RegularExpressions;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>
/// Signs RavensPort in and out of Proton Pass without the user leaving the app.
///
/// The flow this wraps is <c>pass-cli login</c>, which prints a URL and then blocks until the user
/// has opened it and finished authenticating in their browser. So this is the one place the app
/// runs a CLI it expects to sit there for minutes, and the one place it reads a child's output
/// while the child is still alive.
///
/// **The URL is not logged.** Its <c>payload</c> fragment is a live, single-use authentication
/// handle — anyone who opens that link before the user does completes the sign-in as them. It goes
/// to the caller's callback and nowhere else, which is why
/// <see cref="ICliRunner.RunStreamingAsync"/> hands lines back instead of writing them down.
///
/// There is no equivalent for 1Password, and this class deliberately does not pretend otherwise:
/// <c>op</c> has no browser sign-in to drive — it wants a Secret Key and an account password on a
/// terminal — and its licence does not permit RavensPort to ship it. 1Password keeps the desktop-app
/// integration and service-account paths described in <see cref="VaultLockGuidance"/>.
/// </summary>
public sealed partial class ProtonPassAuthenticator(
    ICliRunner cliRunner,
    ProtonPassSession session,
    ProtonPassInstaller installer,
    HelloKeyProtector helloKeyProtector,
    VaultGateService gate,
    ActivityLog activityLog)
{
    /// <summary>Whether this machine can hold the session key behind a Hello gesture.</summary>
    public static Task<bool> IsHelloAvailableAsync() => HelloKeyProtector.IsAvailableAsync();

    /// <summary>
    /// Whether the last sign-in also saved the key behind Hello. Read by the setup page to decide
    /// how firmly to tell the user to write the key down — it still matters either way, since Hello
    /// only covers this PC.
    /// </summary>
    public bool RememberedWithHello { get; private set; }

    /// <summary>Whether a key is already stored that way, so the page can offer to use it.</summary>
    public bool HasHelloKey => HelloKeyProtector.HasProtectedKey(session.SessionDirectory);

    /// <summary>
    /// Prompts for Hello and unlocks the session with the key it returns. The alternative to the
    /// user pasting it.
    ///
    /// **Must be called on the UI thread**, for the same reason as
    /// <see cref="TryRememberWithHelloAsync"/>.
    /// </summary>
    public async Task UnlockWithHelloAsync()
    {
        if (await helloKeyProtector.UnprotectAsync(session.SessionDirectory) is not { Length: > 0 } key)
        {
            throw new VaultCliException(
                "There is no Windows Hello key saved for this session. Paste your session key instead.");
        }

        session.Unlock(key);
        await gate.EvaluateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the key RavensPort currently holds behind Hello, so the next start is a gesture rather
    /// than a paste.
    ///
    /// Best-effort by design, and called from the UI thread straight after a sign-in. It prompts,
    /// and a user who dismisses that prompt has said no to storing the key — not to the sign-in
    /// they just completed. Failing the whole sign-in over it would be reading the wrong answer.
    ///
    /// **Must be called on the UI thread.** The Hello prompt parents itself to the foreground
    /// window; from a thread-pool thread the credential service returns UserCanceled without ever
    /// showing anything, which is indistinguishable from a refusal. Nothing in here uses
    /// ConfigureAwait(false) for that reason.
    /// </summary>
    public async Task<bool> TryRememberWithHelloAsync()
    {
        if (session.CurrentKey is not { Length: > 0 } key) return false;
        if (!await HelloKeyProtector.IsAvailableAsync()) return false;

        try
        {
            await helloKeyProtector.ProtectAsync(session.SessionDirectory, key);
            RememberedWithHello = true;
            return true;
        }
        catch (Exception ex)
        {
            activityLog.Log($"VAULT could not save the session key with Windows Hello: {ex.Message}");
            return false;
        }
    }
    /// <summary>
    /// Finds pass-cli, downloading the pinned release if the machine has none. Returns its path.
    /// </summary>
    public async Task<string> EnsureInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // An existing install always wins — the user's own pass-cli, at whatever version they
        // maintain, is not something the app should quietly route around.
        if (VaultProbe.FindProtonPass() is { } existing && File.Exists(existing)) return existing;

        return await installer.InstallAsync(progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the browser sign-in. Calls <paramref name="onUrl"/> once, with the URL the user has to
    /// open, then returns when they have finished — or throws if they did not.
    /// </summary>
    /// <param name="onUrl">
    /// Raised as soon as the URL appears, which is well before this method returns. That is the
    /// whole point: the user cannot complete a sign-in they have not been shown.
    /// </param>
    public async Task SignInAsync(
        Action<string> onUrl,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!session.HasKey)
        {
            throw new VaultCliException(
                "Set a session key before signing in — RavensPort encrypts its Proton Pass session with it.");
        }

        var exePath = await EnsureInstalledAsync(progress, ct).ConfigureAwait(false);

        progress?.Report("Starting sign-in…");

        var urlSeen = false;
        CliResult result;

        try
        {
            result = await cliRunner.RunStreamingAsync(
                exePath,
                ["login"],
                line =>
                {
                    if (urlSeen || ExtractUrl(line) is not { } url) return;

                    urlSeen = true;
                    onUrl(url);
                    progress?.Report("Waiting for you to finish signing in…");
                },
                session.BuildEnvironment(),
                CliRunner.InteractiveTimeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled or timed out. `login` writes its session files before the browser step
            // completes, so a run that did not finish still leaves a half-session behind — and
            // pass-cli's own `logout` refuses that state ("Session is some but is not logged in"),
            // which would block the next attempt. Clear it here instead.
            session.Wipe();
            throw;
        }

        if (!result.Succeeded)
        {
            session.Wipe();

            var detail = result.FirstErrorLine();
            activityLog.Log($"VAULT Proton Pass sign-in failed with exit {result.ExitCode}");

            throw new VaultCliException(detail.Length > 0
                ? $"Signing in to Proton Pass failed: {detail}"
                : "Signing in to Proton Pass failed.");
        }

        if (!urlSeen)
        {
            // Succeeded without ever printing a URL: possible if a session was already valid.
            // Worth a log line, because it means the UI showed the user nothing to do and they
            // may reasonably wonder what happened.
            activityLog.Log("VAULT Proton Pass sign-in completed without showing a URL");
        }

        activityLog.Log("VAULT signed in to Proton Pass");
        progress?.Report("Signed in. Loading your vault…");

        // Deliberately does NOT offer Windows Hello here, though this is the moment the key is in
        // hand. Every await above uses ConfigureAwait(false), so by this line execution is on a
        // thread-pool thread — and the Hello prompt needs a foreground window to parent itself to.
        // Without one the credential service does not prompt at all: it returns UserCanceled
        // immediately, which would look exactly like the user declining. The caller runs
        // TryRememberWithHelloAsync from the UI thread once this returns.
        await gate.EvaluateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the session: remotely if Proton can be reached, locally regardless, and then puts the
    /// app back to its disconnected state.
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var exePath = VaultProbe.FindProtonPass();

        if (exePath is not null && File.Exists(exePath) && session.HasKey)
        {
            var result = await TryLogoutAsync(exePath, force: false, ct).ConfigureAwait(false);

            if (result?.Succeeded != true)
            {
                // --force skips the remote call. The session then stays listed in the user's Proton
                // account until it expires, so this is the fallback rather than the default — but a
                // sign-out that cannot proceed because Proton is unreachable is worse.
                activityLog.Log("VAULT Proton Pass remote logout failed; clearing the local session");
                await TryLogoutAsync(exePath, force: true, ct).ConfigureAwait(false);
            }
        }

        // Both halves of the Hello arrangement, before the directory goes: Wipe takes the blob with
        // it, but the credential lives in the user's Hello store and would otherwise outlive every
        // trace of what it was for.
        await helloKeyProtector.ForgetAsync(session.SessionDirectory).ConfigureAwait(false);

        // Unconditional, and in this order: the files are worthless without the key, but leaving
        // either behind would let a later "sign in" resume a session the user just ended.
        session.Wipe();
        session.Clear();

        RememberedWithHello = false;
        gate.Disconnect();
    }

    /// <summary>
    /// Throws away the local session without telling Proton — the only recovery available to
    /// someone who has lost their session key.
    ///
    /// <see cref="SignOutAsync"/> cannot help there. Every pass-cli call needs the key: it is what
    /// decrypts the session, so <c>logout</c> without it cannot reach the session it is meant to
    /// end. Running it anyway would be worse than useless — with no session directory to point at,
    /// pass-cli would fall back to the user's own default session and sign *that* out instead.
    ///
    /// So this deletes the files and stops. The session stays live at Proton until it expires, and
    /// the user can revoke it under their Proton account's sessions list if they want it gone
    /// sooner. Nothing in the vault is touched.
    /// </summary>
    public async Task DiscardLocalSessionAsync()
    {
        // The Hello credential goes too. A user who has lost the key has, by definition, no Hello
        // key that opens anything — leaving it would be a prompt that can only ever fail.
        await helloKeyProtector.ForgetAsync(session.SessionDirectory).ConfigureAwait(false);

        session.Wipe();
        session.Clear();

        RememberedWithHello = false;
        activityLog.Log("VAULT discarded the local Proton Pass session — the key that opened it was lost");
    }

    private async Task<CliResult?> TryLogoutAsync(string exePath, bool force, CancellationToken ct)
    {
        try
        {
            return await cliRunner.RunAsync(
                exePath,
                force ? ["logout", "--force"] : ["logout"],
                stdin: null,
                session.BuildEnvironment(),
                CliRunner.WriteTimeout,
                ct).ConfigureAwait(false);
        }
        catch (VaultCliException ex)
        {
            // Never allowed to fail a sign-out. The local state is cleared by the caller either
            // way, and the user asked to be signed out, not to be told why the CLI would not
            // cooperate.
            activityLog.Log($"VAULT Proton Pass logout could not run: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pulls the sign-in URL out of a line of CLI output.
    ///
    /// Matched by shape rather than by the surrounding prose ("Please open the following URL…"),
    /// which is wording a future pass-cli release is free to change. The host is checked, so a
    /// stray link in a warning or a deprecation notice cannot be mistaken for the one the user is
    /// supposed to open.
    /// </summary>
    internal static string? ExtractUrl(string line)
    {
        var match = UrlPattern().Match(line ?? "");
        if (!match.Success) return null;

        var url = match.Value.TrimEnd('.', ',', ')');

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
               && parsed.Scheme == Uri.UriSchemeHttps
               && (parsed.Host == "account.proton.me" || parsed.Host.EndsWith(".proton.me", StringComparison.Ordinal))
            ? url
            : null;
    }

    [GeneratedRegex(@"https://\S+")]
    private static partial Regex UrlPattern();
}
