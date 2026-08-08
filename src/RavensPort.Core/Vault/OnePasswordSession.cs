namespace RavensPort.Core.Vault;

/// <summary>
/// A 1Password service-account token, for as long as the process lives and not one moment longer.
///
/// **Why this exists.** Desktop app integration needs the 1Password GUI running and unlocked, and it
/// reaches it through a library whose lifetime this app cannot control — see
/// <see cref="VaultAuthorization.IsUnreachable"/> for what that costs. A service account has neither
/// problem: the SDK routes a token to its own embedded core and talks to 1Password over the network,
/// so nothing local has to be running, unlocked, or asked. That is the only way to run this app on a
/// machine nobody is sitting at.
///
/// **Why nothing is stored.** The obvious convenience — remember the token so the user need not
/// paste it again — is the one thing this class must not do. A service-account token is a bearer
/// credential for every vault it was granted, it does not expire on its own, and it is not bound to
/// this machine. Written to disk it would be a copy of the user's access sitting outside their
/// password manager, which is precisely what RavensPort exists to avoid. So it is held here, in
/// memory, and asked for again after every restart. The cost is real and belongs on screen: an
/// install that starts at login serves nothing until someone types the token in.
///
/// **Why not an environment variable.** <c>OP_SERVICE_ACCOUNT_TOKEN</c> would survive restarts,
/// which is the same as storing it — in a place readable by every process the user runs, and
/// frequently in a shell profile committed to somewhere. Deliberately not read.
///
/// The token leaves this object by exactly one route, <see cref="BuildEnvironment"/>, which puts it
/// in a child process's environment block. Never an argument: a Windows command line is readable by
/// any process in the session. Never a log line, never the screen, never the vault.
///
/// A <c>string</c> rather than a <see cref="System.Security.SecureString"/>, for the same reason
/// <see cref="ProtonPassSession"/> gives: it has to reach <c>ProcessStartInfo.Environment</c>, which
/// takes a string, so a SecureString would be decrypted into the managed heap at that point anyway
/// and buy nothing but the appearance of care. .NET strings cannot be reliably zeroed, so this value
/// is recoverable from a memory dump for as long as the app is connected — acceptable, because
/// anything that can dump this process can also read the decrypted vault it is holding.
/// </summary>
public sealed class OnePasswordSession
{
    /// <summary>
    /// What 1Password's service-account tokens start with. Checked only to catch the obvious
    /// mistake — an account name, a password, half a copy — because the real verdict comes from
    /// 1Password and a client-side format rule that guesses wrong locks the user out of their own
    /// app for no reason.
    /// </summary>
    private const string ExpectedPrefix = "ops_";

    private string? _token;

    public bool HasToken => _token is { Length: > 0 };

    /// <summary>
    /// The token itself. Internal, and read by nothing in the app: every caller has
    /// <see cref="BuildEnvironment"/>, which puts it where it belongs without returning it. Kept for
    /// the tests that assert exactly that.
    /// </summary>
    internal string? CurrentToken => _token;

    /// <summary>
    /// Accepts the token the user pasted. Whitespace-trimmed, because copy buttons and password
    /// managers add it, and a trailing newline is not a reason to tell someone their token is wrong.
    /// </summary>
    public void Unlock(string? token)
    {
        var trimmed = (token ?? "").Trim();

        if (trimmed.Length == 0)
        {
            throw new VaultCliException("A 1Password service account token is required.");
        }

        if (!trimmed.StartsWith(ExpectedPrefix, StringComparison.Ordinal))
        {
            throw new VaultCliException(
                "That does not look like a 1Password service account token — they begin with "
                + $"\"{ExpectedPrefix}\". Create one in 1Password under Developer > Service Accounts, "
                + "and grant it access to the vault RavensPort should use.");
        }

        _token = trimmed;
    }

    /// <summary>Forgets the token. Nothing was written down, so there is nothing else to undo.</summary>
    public void Clear() => _token = null;

    /// <summary>
    /// What a child process needs in order to act as this service account. Empty when there is no
    /// token, so a caller that has not connected yet produces an honest "not signed in" rather than
    /// a confusing failure from the CLI.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildEnvironment() =>
        _token is { Length: > 0 } token
            ? new Dictionary<string, string> { ["OP_SERVICE_ACCOUNT_TOKEN"] = token }
            : new Dictionary<string, string>();
}
