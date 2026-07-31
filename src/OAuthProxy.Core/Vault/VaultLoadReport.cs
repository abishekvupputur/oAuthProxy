namespace OAuthProxy.Core.Vault;

/// <summary>
/// What a load had to say about itself: what could not be read, and what was dropped to make the
/// configuration match what is actually in the vault.
///
/// The two are kept apart because they mean different things to the caller. A warning is
/// informational — the store is what the vault says. A removal means the store no longer matches
/// the note it was loaded from, so the note has to be rewritten, or the next launch would raise
/// the same ghost again.
/// </summary>
public sealed class VaultLoadReport
{
    /// <summary>Things the user should know that changed nothing.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Records dropped because the vault item holding their secret is gone.</summary>
    public List<string> Removals { get; } = [];

    public bool HasAnything => Warnings.Count > 0 || Removals.Count > 0;

    /// <summary>Everything worth telling the user, in one line for a log or a banner.</summary>
    public string Message => string.Join(" ", Removals.Concat(Warnings));
}
