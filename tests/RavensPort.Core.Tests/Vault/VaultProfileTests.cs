using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The vault naming rule. It carries more than tidiness: the setup page decides which vaults it is
/// entitled to list from the name alone, so a rule that drifts either hides the user's own
/// RavensPort vaults from them or starts reciting the rest of their password manager back at
/// them.
/// </summary>
public class VaultProfileTests
{
    [Fact]
    public void AnEmptyProfileMakesTheDefaultVault()
    {
        // The common case: one profile, no suffix, and the name the app has always looked for.
        Assert.Equal(VaultConstants.VaultName, VaultProfile.NameFor(null));
        Assert.Equal(VaultConstants.VaultName, VaultProfile.NameFor(""));
        Assert.Equal(VaultConstants.VaultName, VaultProfile.NameFor("   "));
    }

    [Fact]
    public void AProfileIsAppendedToTheDefaultName()
    {
        Assert.Equal($"{VaultConstants.VaultName} Work", VaultProfile.NameFor("Work"));

        // Trimmed, because the space around it is invisible in the box and would produce a vault
        // whose name the user cannot reproduce.
        Assert.Equal($"{VaultConstants.VaultName} Personal", VaultProfile.NameFor("  Personal  "));
    }

    [Fact]
    public void OnlyVaultsNamedAfterTheDefaultAreRecognised()
    {
        Assert.True(VaultProfile.Matches(VaultConstants.VaultName));
        Assert.True(VaultProfile.Matches($"{VaultConstants.VaultName} Work"));

        // Case-insensitive and position-independent: the user typed this name into their password
        // manager, and being told their own vault does not exist over capitalisation would be a
        // poor way to spend their time.
        Assert.True(VaultProfile.Matches("RAVENSPORT work"));
        Assert.True(VaultProfile.Matches("Work RavensPort"));

        Assert.False(VaultProfile.Matches("Personal"));
        Assert.False(VaultProfile.Matches(null));
    }

    [Fact]
    public void TheProfileCanBeReadBackOutOfAVaultName()
    {
        Assert.Equal("Work", VaultProfile.ProfileOf($"{VaultConstants.VaultName} Work"));

        // The default vault has no profile, and a vault named something else has none to report.
        Assert.Null(VaultProfile.ProfileOf(VaultConstants.VaultName));
        Assert.Null(VaultProfile.ProfileOf("Agents"));
    }
}
