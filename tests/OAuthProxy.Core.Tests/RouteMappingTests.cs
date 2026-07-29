using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// A route used to hold exactly one credential in four scalar fields; it now holds a list. Both
/// shapes have to resolve to the same answer, because a store written by an older build is read
/// by this one and must keep forwarding identically.
/// </summary>
public class RouteMappingTests
{
    [Fact]
    public void EffectiveCredentials_PrefersTheListWhenItHasEntries()
    {
        var listed = Guid.NewGuid();
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            Credentials = [RouteCredential.For(listed, CredentialPlacement.Query)],
            CredentialId = Guid.NewGuid(),
        };

        var credential = Assert.Single(route.EffectiveCredentials);
        Assert.Equal(listed, credential.CredentialId);
    }

    [Fact]
    public void EffectiveCredentials_TranslatesTheSupersededFieldsWhenTheListIsEmpty()
    {
        var id = Guid.NewGuid();
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            CredentialId = id,
            CredentialPlacement = CredentialPlacement.Body,
            CredentialParameterName = "auth_token",
            CredentialValuePrefix = "token ",
        };

        var credential = Assert.Single(route.EffectiveCredentials);
        Assert.Equal(id, credential.CredentialId);
        Assert.Equal(CredentialPlacement.Body, credential.Placement);
        Assert.Equal("auth_token", credential.ParameterName);
        Assert.Equal("token ", credential.ValuePrefix);
    }

    [Fact]
    public void EffectiveCredentials_SupersededFieldsWithNoPlacement_MeanBearerInAHeader()
    {
        // What a route written before placements existed meant, and the only shape it could have.
        var route = new RouteMapping { PathPrefix = "/app/api", CredentialId = Guid.NewGuid() };

        var credential = Assert.Single(route.EffectiveCredentials);
        Assert.Equal(CredentialInjection.BearerHeader, credential.ToCredentialInjection());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void EffectiveCredentials_NoListAndNoCredentialId_MeansAttachNothing(string? credentialId)
    {
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            CredentialId = credentialId is null ? null : Guid.Parse(credentialId),
        };

        Assert.Empty(route.EffectiveCredentials);
    }

    [Fact]
    public void Normalize_ClearsTheSupersededFieldsAndIsIdempotent()
    {
        var id = Guid.NewGuid();
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            CredentialId = id,
            CredentialPlacement = CredentialPlacement.Query,
            CredentialParameterName = "access_token",
            CredentialValuePrefix = "",
        };

        route.Normalize().Normalize();

        Assert.Null(route.CredentialId);
        Assert.Null(route.CredentialPlacement);
        Assert.Null(route.CredentialParameterName);
        Assert.Null(route.CredentialValuePrefix);

        var credential = Assert.Single(route.Credentials);
        Assert.Equal(id, credential.CredentialId);
        Assert.Equal(CredentialPlacement.Query, credential.Placement);
    }

    [Fact]
    public void Normalize_LeavesAnAlreadyListedRouteAlone()
    {
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            Credentials =
            [
                RouteCredential.For(Guid.NewGuid(), CredentialPlacement.Header),
                RouteCredential.For(Guid.NewGuid(), CredentialPlacement.Query),
            ],
        };
        var before = route.Credentials.ToList();

        route.Normalize();

        Assert.Equal(before.Select(c => c.CredentialId), route.Credentials.Select(c => c.CredentialId));
    }

    [Theory]
    [InlineData(CredentialPlacement.Header, "Authorization", "Bearer ")]
    [InlineData(CredentialPlacement.Query, "access_token", "")]
    [InlineData(CredentialPlacement.Body, "access_token", "")]
    public void RouteCredentialFor_UsesThePlacementsDefaults(
        CredentialPlacement placement, string name, string prefix)
    {
        var credential = RouteCredential.For(Guid.NewGuid(), placement);

        Assert.Equal(name, credential.ParameterName);
        Assert.Equal(prefix, credential.ValuePrefix);
    }

    [Fact]
    public void DescribeCredentials_SaysSoExplicitlyWhenThereAreNone()
    {
        // A route that attaches nothing has to read as a decision, not as an empty field.
        Assert.Contains("no credential", new RouteMapping { PathPrefix = "/app/api" }.DescribeCredentials());
    }

    [Fact]
    public void DescribeCredentials_NamesEveryCredentialAndWhereItGoes()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            Credentials =
            [
                RouteCredential.For(first, CredentialPlacement.Header),
                RouteCredential.For(second, CredentialPlacement.Query),
            ],
        };

        var described = route.DescribeCredentials(id => id == first ? "alpha" : "bravo");

        Assert.Contains("alpha as header Authorization", described);
        Assert.Contains("bravo as query ?access_token=", described);
    }
}
