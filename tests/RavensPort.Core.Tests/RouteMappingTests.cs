using RavensPort.Core.Models;

namespace RavensPort.Core.Tests;

/// <summary>
/// A route attaches a list of credentials, each with its own placement. These pin the defaults a
/// placement implies and the description the UI and logs read back.
/// </summary>
public class RouteMappingTests
{
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
