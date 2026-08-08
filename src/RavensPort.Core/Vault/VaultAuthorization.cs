namespace RavensPort.Core.Vault;

/// <summary>
/// Reads a password manager's own error text for the two things the retry logic has to tell apart:
/// a connection that has gone stale, and a user who has said no.
///
/// Both arrive as ordinary failures with nothing but a message to go on, and the right response to
/// each is the opposite of the other. A stale connection should be rebuilt and the call repeated —
/// the user did nothing wrong and need not be involved. A refusal must be left alone: reaching the
/// vault again is what raises the prompt, so retrying a decline is a program arguing with someone
/// who has already answered.
///
/// Matched on substrings because that is all these SDKs give. Both matchers therefore fail towards
/// "ordinary failure", which is the harmless direction: an unrecognised stale-client message costs
/// a save that waits for the user instead of healing itself, and an unrecognised decline costs the
/// prompt pacing we had before any of this existed.
/// </summary>
public static class VaultAuthorization
{
    /// <summary>
    /// Whether the SDK is saying its client handle is dead, as opposed to refusing this particular
    /// operation.
    ///
    /// The 1Password Go SDK hands out a client id when <c>NewClient</c> connects to the desktop app,
    /// and the core invalidates it when that authorization goes away. Every later call then returns
    /// <c>invalid client id</c> in no time at all, because it never reaches 1Password. Nothing about
    /// the handle recovers on its own, which is what left the app stuck until the vault was
    /// disconnected and reconnected: <c>op item list --vault … -> exit 1 in 0ms (invalid client id)</c>,
    /// forever.
    /// </summary>
    public static bool ClientIsDead(string? message) =>
        Says(message, "invalid client id") || Says(message, "client not initialized");

    /// <summary>
    /// Whether the user was asked to authorize and declined — the 1Password "allow RavensPort to
    /// connect" prompt dismissed or timed out.
    ///
    /// This is an answer, not a fault, and it is the one failure that must not be retried on a
    /// timer. Each attempt raises the prompt again, so a decline followed by a retry loop is the
    /// app asking the same question every few seconds — which is what a user reported as it
    /// "keeps pushing notifications", with the exponential backoff still only part-way up its ramp.
    /// </summary>
    public static bool WasDeclined(string? message) =>
        Says(message, "denied authorization")
        || Says(message, "authorization denied")
        || Says(message, "authorization was denied")
        || Says(message, "user declined")
        || Says(message, "request was declined");

    /// <summary>
    /// Whether 1Password cannot be reached at all, as opposed to reaching it and being refused.
    ///
    /// Desktop app integration listens on <c>\\.\pipe\1password-sdk-integrations</c>. 1Password opens
    /// that pipe <b>when the app starts</b>, provided Settings > Developer > "Integrate with other
    /// apps" is on, and keeps it open for the life of the process. Opening a pipe that was never
    /// published is an ordinary <c>CreateFileW</c> against a path that does not exist, so Windows
    /// answers ERROR_FILE_NOT_FOUND and the SDK passes it through as the least informative sentence
    /// it owns: <c>The system cannot find the file specified.</c>
    ///
    /// Measured, not guessed, because the obvious readings are all wrong:
    ///
    /// <list type="bullet">
    /// <item>Not a lock. The pipe survives locking, and a locked 1Password answers on it — with a
    /// real SDK error, never this one.</item>
    /// <item>Not a stale handle. A freshly started process with no cached state of any kind fails
    /// identically on its first call, so there is nothing to rebuild.</item>
    /// <item>Not the app restarting. A restarted 1Password has the pipe before anyone unlocks it.</item>
    /// </list>
    ///
    /// The case that actually produces it — beyond 1Password simply not running — is switching the
    /// integration on inside an already-running app. The setting is saved, the pipe is not created
    /// retroactively, and nothing recovers until 1Password is restarted. A user lost an evening to
    /// exactly that, with the app blaming itself in the log the whole time, which is why the message
    /// this feeds names the restart specifically.
    ///
    /// Never drives a reconnect. There is nothing on the other end to connect to, so rebuilding
    /// would burn an attempt to arrive at the same answer. Retrying is free and silent — it fails
    /// without reaching anyone — so the ordinary queue simply keeps the change and saves it the
    /// moment 1Password is there.
    ///
    /// The Win32 wording is broad, and matching it is safe only because of where it is asked.
    /// <see cref="NativeCliRunner"/> launches no processes and opens no files, so within it the only
    /// file that can be missing is the pipe. It must not be reused anywhere that runs an executable
    /// — there the same message means the CLI itself is missing, which no amount of waiting fixes.
    /// <see cref="CliRunner"/> is that place, and does not call this.
    /// </summary>
    public static bool IsUnreachable(string? message) =>
        Says(message, "the system cannot find the file specified");

    private static bool Says(string? message, string phrase) =>
        message is not null && message.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
