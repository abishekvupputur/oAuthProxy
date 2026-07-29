using System.Net;
using System.Text;
using OAuthProxy.Core.Mcp;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;

namespace OAuthProxy.Core.Tests.Mcp;

/// <summary>
/// The funnel adds two things ahead of the reverse proxy: a gate middleware that inspects every
/// request, and an endpoint that owns a path prefix. Both are positioned to break ordinary
/// proxying if they are wrong about what belongs to them.
///
/// These are the "nothing else changed" tests: routes keep working with the funnel enabled and
/// disabled, the gate lets non-funnel traffic past untouched, and the funnel's endpoints do not
/// shadow a route whose prefix merely looks similar.
/// </summary>
public class ProxyUnaffectedByFunnelTests : IAsyncLifetime
{
    private const string Token = "UNAFFECTED-ACCESS-TOKEN";

    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Reused purely as a convenient HTTP server that echoes what it received; this suite is
        // about ordinary proxying, not MCP.
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var credential = new CredentialRecord
        {
            Name = "c",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };
        var upstreamRecord = new UpstreamRecord { Name = "u", BaseUrl = _upstream.Url };

        await _host.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstreamRecord);

            void AddRoute(string prefix) => store.Routes.Add(new RouteMapping
            {
                PathPrefix = prefix,
                UpstreamId = upstreamRecord.Id,
                CredentialId = credential.Id,
                StripPrefix = true,
            });

            AddRoute("/api");

            // Prefixes that share a leading substring with the funnel's "/mcp" but are different
            // segments. If the gate or the endpoint matched on characters rather than segments,
            // these would be swallowed.
            AddRoute("/mcpsrv");
            AddRoute("/mcp-tools");
        });
        _host.RebuildProxyConfig();

        _client = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/mcpsrv")]
    [InlineData("/mcp-tools")]
    public async Task RoutesAreServedWithTheFunnelEnabled(string prefix)
    {
        var response = await PostAsync(prefix);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(_upstream.ReceivedAuthorization);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/mcpsrv")]
    [InlineData("/mcp-tools")]
    public async Task RoutesAreServedWithTheFunnelDisabled(string prefix)
    {
        // The master toggle must close the funnel's own endpoints and nothing else.
        await _host.MutateAsync(store => store.Settings.McpFunnelEnabled = false);

        var response = await PostAsync(prefix);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheGateLeavesOrdinaryTrafficUntouched()
    {
        // Header stripping, token injection, and query preservation all still happen with the
        // gate sitting in front of them.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api?page=2")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var headers = Assert.Single(_upstream.ReceivedHeaders);
        Assert.Equal($"Bearer {Token}", headers["Authorization"]);
        Assert.False(headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName));
    }

    [Fact]
    public async Task AFunnelEndpointDoesNotShadowASimilarlyNamedRoute()
    {
        // "/mcp-tools" and "/mcpsrv" are not inside "/mcp"; a prefix match on the raw string
        // would have handed both to the funnel, which would answer 404 for an unknown slug.
        await _host.AddRemoteSourceAsync("up", _upstream.Url);
        await _host.AddFunnelAsync("real", new McpFunnelSource
        {
            SourceId = _host.Cache.Current.McpSources.Single().Id,
        });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/mcp-tools")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/mcpsrv")).StatusCode);

        // ...while the funnel's own path still belongs to the funnel.
        Assert.NotEqual(HttpStatusCode.NotFound, (await PostAsync("/mcp/real")).StatusCode);
    }

    [Fact]
    public async Task AnHtmlAnswerToAnApiCallIsCalledOutInTheLog()
    {
        // The case that cost an afternoon: an upstream answering 200 with a sign-in or landing
        // page. Every log line said 200, the client reported "response completed without a
        // reply", and nothing connected the two. The media type is metadata, so logging it
        // leaks nothing while making that failure obvious at a glance.
        _upstream.RespondWithHtml = true;

        await PostAsync("/api");

        Assert.Contains(
            _host.ActivityLog.GetRecent(50),
            line => line.Contains("[text/html]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJsonAnswerIsNotAnnotated()
    {
        // The annotation has to stay rare enough to mean something.
        await PostAsync("/api");

        Assert.DoesNotContain(
            _host.ActivityLog.GetRecent(50),
            line => line.Contains("[application/json]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnmatchedPathIsStillANotFoundRatherThanAFunnelResponse()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/nothing-here")).StatusCode);
    }

    [Fact]
    public async Task RouteTrafficIsStillRefusedWithoutTheLocalApiKey()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };

        var response = await anonymous.PostAsync("/api",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_upstream.ReceivedHeaders);
    }

    [Fact]
    public async Task TheFunnelHopMarkerDoesNotBlockOrdinaryRouteTraffic()
    {
        // The loop guard applies to funnel endpoints only. A route is exactly where a funnel's
        // own requests are supposed to land, so this header must pass through here.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);
        request.Headers.Add(LocalAccessGuard.FunnelHopHeaderName, "1");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ...but it is still stripped before reaching the upstream.
        var headers = Assert.Single(_upstream.ReceivedHeaders);
        Assert.False(headers.ContainsKey(LocalAccessGuard.FunnelHopHeaderName));
    }

    private Task<HttpResponseMessage> PostAsync(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        return _client.SendAsync(request);
    }
}
