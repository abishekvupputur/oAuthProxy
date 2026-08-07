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

    private static bool Says(string? message, string phrase) =>
        message is not null && message.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
