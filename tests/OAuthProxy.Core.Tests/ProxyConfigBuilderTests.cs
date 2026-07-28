using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;

namespace OAuthProxy.Core.Tests;

public class ProxyConfigBuilderTests
{
    [Fact]
    public void Build_EnabledRouteWithKnownUpstream_ProducesRouteAndCluster()
    {
        var upstream = new UpstreamRecord { Name = "httpbin", BaseUrl = "https://httpbin.org" };
        var credentialId = Guid.NewGuid();
        var route = new RouteMapping
        {
            PathPrefix = "/app/httpbin",
            UpstreamId = upstream.Id,
            CredentialId = credentialId,
            StripPrefix = true,
            Enabled = true,
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        var routeConfig = Assert.Single(routes);
        Assert.Equal(route.Id.ToString(), routeConfig.RouteId);
        Assert.Equal(route.Id.ToString(), routeConfig.ClusterId);
        Assert.Equal("/app/httpbin/{**catch-all}", routeConfig.Match.Path);
        Assert.Equal(credentialId.ToString(), routeConfig.Metadata![ProxyConfigBuilder.CredentialIdMetadataKey]);
        Assert.NotNull(routeConfig.Transforms);

        var clusterConfig = Assert.Single(clusters);
        Assert.Equal(route.Id.ToString(), clusterConfig.ClusterId);
        Assert.Equal("https://httpbin.org", clusterConfig.Destinations!["d1"].Address);
    }

    [Fact]
    public void Build_DisabledRoute_IsExcluded()
    {
        var upstream = new UpstreamRecord { Name = "httpbin", BaseUrl = "https://httpbin.org" };
        var route = new RouteMapping
        {
            PathPrefix = "/app/httpbin",
            UpstreamId = upstream.Id,
            CredentialId = Guid.NewGuid(),
            Enabled = false,
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_RouteWithMissingUpstream_IsExcluded()
    {
        var route = new RouteMapping
        {
            PathPrefix = "/app/missing",
            UpstreamId = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(),
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], []);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }
}
