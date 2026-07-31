namespace RavensPort.Core.Vault;

/// <summary>
/// The naming rule for RavensPort's vaults: <c>RavensPort</c> on its own, or
/// <c>RavensPort Work</c> for a named profile.
///
/// One vault per profile is how a single install keeps separate sets of credentials, routes and
/// funnels apart, and the shared prefix is what makes them recognisable — both to the user
/// scanning their password manager and to this app, which uses it to decide which vaults it is
/// entitled to offer. A picker that listed every vault in the account would be asking the user to
/// point a credential store at their personal one, and would read as an app rummaging through
/// things that are none of its business.
/// </summary>
public static class VaultProfile
{
    /// <summary>
    /// Whether a vault name is one of RavensPort's by name alone. Contains rather than
    /// starts-with: a user who called theirs "Work RavensPort" meant the same thing, and
    /// hiding it from their own picker would be a puzzle rather than a safeguard.
    /// </summary>
    public static bool Matches(string? vaultName) =>
        vaultName is not null
        && vaultName.Contains(VaultConstants.VaultName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The vault name for a profile. Blank means the unsuffixed default, which is the whole point
    /// of the profile being optional — most people have exactly one.
    /// </summary>
    public static string NameFor(string? profile) =>
        string.IsNullOrWhiteSpace(profile)
            ? VaultConstants.VaultName
            : $"{VaultConstants.VaultName} {profile.Trim()}";

    /// <summary>
    /// The profile part of a vault name, or null for the default vault and for anything not named
    /// after the prefix. For display only — the name is what identifies the vault.
    /// </summary>
    public static string? ProfileOf(string? vaultName)
    {
        var name = vaultName?.Trim() ?? "";
        if (!name.StartsWith(VaultConstants.VaultName, StringComparison.OrdinalIgnoreCase)) return null;

        var profile = name[VaultConstants.VaultName.Length..].Trim();
        return profile.Length == 0 ? null : profile;
    }
}
