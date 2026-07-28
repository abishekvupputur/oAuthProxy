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

    [Fact]
    public void Build_RouteWithUnparseablePrefix_IsExcludedWithoutTakingOtherRoutesDown()
    {
        // A prefix containing '{' produces a template RoutePatternFactory cannot parse. Handed
        // to YARP it makes the whole config update fail, so the good route's pending edit would
        // be discarded too. Dropping just the bad one keeps the rest applying.
        var upstream = new UpstreamRecord { Name = "echo", BaseUrl = "https://api.test" };

        var good = new RouteMapping { PathPrefix = "/good", UpstreamId = upstream.Id, CredentialId = Guid.NewGuid() };
        var bad = new RouteMapping { PathPrefix = "/bad{x}", UpstreamId = upstream.Id, CredentialId = Guid.NewGuid() };

        var (routes, clusters) = ProxyConfigBuilder.Build([good, bad], [upstream]);

        Assert.Single(routes);
        Assert.Single(clusters);
        Assert.Equal("/good/{**catch-all}", routes[0].Match.Path);
    }

    [Fact]
    public void Build_RouteWithDotSegmentPrefix_IsExcluded()
    {
        var upstream = new UpstreamRecord { Name = "echo", BaseUrl = "https://api.test" };
        var route = new RouteMapping { PathPrefix = "/api/../admin", UpstreamId = upstream.Id, CredentialId = Guid.NewGuid() };

        var (routes, _) = ProxyConfigBuilder.Build([route], [upstream]);

        Assert.Empty(routes);
    }
}
