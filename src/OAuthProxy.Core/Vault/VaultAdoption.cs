namespace OAuthProxy.Core.Vault;

/// <summary>
/// Thrown when a vault the user named cannot be used. The message is shown verbatim on the setup
/// page, so it says what is wrong with that particular vault rather than "invalid".
/// </summary>
public sealed class VaultAdoptionException(string message) : Exception(message);

/// <summary>What is left to do after a vault the user named has been accepted.</summary>
public enum VaultAdoptionOutcome
{
    /// <summary>It already holds a readable OAuthProxy configuration. Use it as it is.</summary>
    AlreadyConfigured,

    /// <summary>It is empty, so it has to be stamped with a config note to become OAuthProxy's.</summary>
    Empty,
}

/// <summary>
/// Whether a vault the user named may be used, in the same words for both backends.
///
/// Only two kinds are safe to take over: one OAuthProxy already wrote, and an empty one the user
/// made for it. Anything else is a vault with the user's own things in it, and using it would put
/// those entries within reach of this app's delete reconciliation — the very thing the item-name
/// prefix exists to keep them out of. Refusing here is far cheaper than explaining a deleted
/// password afterwards.
/// </summary>
public static class VaultAdoption
{
    public static VaultAdoptionOutcome Judge(string vaultName, int itemCount, string? configNote)
    {
        if (configNote is not null)
        {
            // The note is free text the user can edit in their password manager, so an unreadable
            // one is a mistake rather than corruption — but it is also the only evidence that this
            // vault is OAuthProxy's, so it cannot be waved through.
            return VaultDocument.TryParse(configNote) is not null
                ? VaultAdoptionOutcome.AlreadyConfigured
                : throw new VaultAdoptionException(
                    $"'{vaultName}' has an '{VaultItemNaming.ConfigTitle}' item, but it could not be read as an "
                    + "OAuthProxy configuration. Repair or delete that item, or point OAuthProxy at an empty vault.");
        }

        if (itemCount > 0)
        {
            throw new VaultAdoptionException(
                $"'{vaultName}' already has {itemCount} item(s) in it and no OAuthProxy configuration. "
                + "Use an empty vault, or one OAuthProxy has written to before — anything else would put your "
                + "own entries in reach of this app's housekeeping.");
        }

        return VaultAdoptionOutcome.Empty;
    }

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
        new("Type the name of the vault OAuthProxy should use.");
}
