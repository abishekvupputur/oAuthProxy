using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// Titles are the fallback identity for every item. The index in the config note is only a cache;
/// when it is stale or missing, parsing the guid back out of a title is the only thing standing
/// between the user and a vault full of orphans.
/// </summary>
public class VaultItemNamingTests
{
    [Theory]
    [InlineData(VaultItemRole.Credential)]
    [InlineData(VaultItemRole.RouteKey)]
    [InlineData(VaultItemRole.FunnelKey)]
    public void EveryTitleRoundTripsItsRoleAndId(VaultItemRole role)
    {
        var id = Guid.NewGuid();
        var title = role switch
        {
            VaultItemRole.Credential => VaultItemNaming.ForCredential(id, "Google Drive"),
            VaultItemRole.RouteKey => VaultItemNaming.ForRouteKey(id, "/gdrive"),
            _ => VaultItemNaming.ForFunnelKey(id, "coding-agent"),
        };

        Assert.True(VaultItemNaming.TryParse(title, out var parsedRole, out var parsedId));
        Assert.Equal(role, parsedRole);
        Assert.Equal(id, parsedId);
    }

    [Fact]
    public void TheConfigNoteParsesAsItsOwnRoleWithNoId()
    {
        Assert.True(VaultItemNaming.TryParse(VaultItemNaming.ConfigTitle, out var role, out _));
        Assert.Equal(VaultItemRole.Config, role);
    }

    [Theory]
    [InlineData("My bank login")]
    [InlineData("OAuthProxy but nothing else")]
    [InlineData("OAuthProxy credential — no guid here")]
    [InlineData("OAuthProxy credential — bad [not-a-guid]")]
    public void AnythingElseIsNotOurs(string title)
    {
        Assert.False(VaultItemNaming.TryParse(title, out _, out _));
    }

    [Fact]
    public void ANameWithNewlinesCannotBreakTheTitle()
    {
        // Titles are one line to these CLIs. A pasted multi-line name would otherwise produce an
        // item the parser could never match again.
        var id = Guid.NewGuid();
        var title = VaultItemNaming.ForCredential(id, "line one\r\nline two");

        Assert.DoesNotContain('\n', title);
        Assert.DoesNotContain('\r', title);
        Assert.True(VaultItemNaming.TryParse(title, out _, out var parsedId));
        Assert.Equal(id, parsedId);
    }

    [Fact]
    public void ANameContainingBracketsCannotForgeAGuidSuffix()
    {
        // The suffix is the identity, so a name ending in something bracket-shaped must not be
        // able to shadow it.
        var real = Guid.NewGuid();
        var decoy = Guid.NewGuid();
        var title = VaultItemNaming.ForCredential(real, $"sneaky [{decoy:D}]");

        Assert.True(VaultItemNaming.TryParse(title, out _, out var parsedId));
        Assert.Equal(real, parsedId);
    }

    [Fact]
    public void AVeryLongNameIsTruncatedButStillParses()
    {
        var id = Guid.NewGuid();
        var title = VaultItemNaming.ForCredential(id, new string('x', 500));

        Assert.True(title.Length < 200);
        Assert.True(VaultItemNaming.TryParse(title, out _, out var parsedId));
        Assert.Equal(id, parsedId);
    }

    [Fact]
    public void AnEmptyNameStillProducesAParseableTitle()
    {
        var id = Guid.NewGuid();

        Assert.True(VaultItemNaming.TryParse(VaultItemNaming.ForCredential(id, "   "), out _, out var parsedId));
        Assert.Equal(id, parsedId);
    }
}
