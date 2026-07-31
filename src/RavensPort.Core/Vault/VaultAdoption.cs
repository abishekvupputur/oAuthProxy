namespace RavensPort.Core.Vault;

/// <summary>
/// Thrown when a vault the user named cannot be used. The message is shown verbatim on the setup
/// page, so it says what is wrong with that particular vault rather than "invalid".
/// </summary>
public sealed class VaultAdoptionException(string message) : Exception(message);

/// <summary>What is left to do after a vault the user named has been accepted.</summary>
public enum VaultAdoptionOutcome
{
    /// <summary>It already holds a readable RavensPort configuration. Use it as it is.</summary>
    AlreadyConfigured,

    /// <summary>It is empty, so it has to be stamped with a config note to become RavensPort's.</summary>
    Empty,
}

/// <summary>
/// Whether a vault the user named may be used, in the same words for both backends.
///
/// Only two kinds are safe to take over: one RavensPort already wrote, and an empty one the user
/// made for it. Anything else is a vault with the user's own things in it, and using it would put
/// those entries within reach of this app's delete reconciliation — the very thing the item-name
/// prefix exists to keep them out of. Refusing here is far cheaper than explaining a deleted
/// password afterwards.
/// </summary>
public static class VaultAdoption
{
    /// <param name="titles">
    /// Every live item in the vault, this app's and the user's alike. Counted in full — a vault is
    /// only "empty" when nothing at all is in it — and named back in the refusal, because "it has
    /// items in it" about a vault the user believes they emptied is impossible to argue with
    /// otherwise.
    /// </param>
    public static VaultAdoptionOutcome Judge(string vaultName, IReadOnlyCollection<string> titles, string? configNote)
    {
        var itemCount = titles.Count;

        if (configNote is not null)
        {
            // The note is free text the user can edit in their password manager, so an unreadable
            // one is a mistake rather than corruption — but it is also the only evidence that this
            // vault is RavensPort's, so it cannot be waved through.
            return VaultDocument.TryParse(configNote) is not null
                ? VaultAdoptionOutcome.AlreadyConfigured
                : throw new VaultAdoptionException(
                    $"'{vaultName}' has an '{VaultItemNaming.ConfigTitle}' item, but it could not be read as an "
                    + "RavensPort configuration. Repair or delete that item, or point RavensPort at an empty vault.");
        }

        if (itemCount > 0)
        {
            var examples = string.Join(", ", titles.Take(3).Select(title => $"'{Shorten(title)}'"));
            var andMore = itemCount > 3 ? $", and {itemCount - 3} more" : "";

            throw new VaultAdoptionException(
                $"'{vaultName}' already has {itemCount} item(s) in it and no RavensPort configuration: "
                + $"{examples}{andMore}. Use an empty vault, or one RavensPort has written to before — anything "
                + "else would put your own entries in reach of this app's housekeeping.");
        }

        return VaultAdoptionOutcome.Empty;
    }

    /// <summary>
    /// The picker's version of <see cref="Judge"/>: whether a vault is worth offering at all,
    /// decided from item titles alone.
    ///
    /// Offering a vault that <see cref="Judge"/> will refuse is a trap — the user picks the thing
    /// the app showed them and is told no — so the list is filtered by the same rule that decides
    /// the answer. Deliberately the optimistic half of it: this does not fetch the config note, so
    /// a vault whose note has been broken still appears, and is refused on selection with the one
    /// message that can explain why.
    /// </summary>
    public static bool LooksAdoptable(IReadOnlyCollection<string> titles) =>
        titles.Count == 0 || titles.Contains(VaultItemNaming.ConfigTitle);

    /// <summary>The "no such vault" message, listing what does exist so a typo is obvious.</summary>
    public static VaultAdoptionException NoSuchVault(string vaultName, IEnumerable<string> available)
    {
        var names = string.Join(", ", available.Select(name => $"'{name}'"));

        return new VaultAdoptionException(
            names.Length == 0
                ? $"There is no vault called '{vaultName}'."
                : $"There is no vault called '{vaultName}'. This account has: {names}.");
    }

    public static VaultAdoptionException NameRequired() =>
        new("Type the name of the vault RavensPort should use.");

    /// <summary>Keeps one long title from swallowing the message.</summary>
    private static string Shorten(string title) =>
        title.Length <= 40 ? title : string.Concat(title.AsSpan(0, 37), "…");
}
