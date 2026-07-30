using ModelContextProtocol.Client;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Tests.Mcp;

/// <summary>
/// The reason the funnel lives inside this app rather than beside it: a source can be an MCP
/// server that needs an OAuth token, and the proxy already holds one.
///
/// A route-backed source is dialled back through the app's own loopback listener, so the request
/// takes the ordinary proxied path — LocalAccessGuard, YARP, the credential transform — and picks
/// the token up on the way. These tests prove the token really arrives, and that the funnel's own
/// signalling does not leak past the hop.
/// </summary>
public class McpFunnelRouteSourceTests : IAsyncLifetime
{
    private const string Token = "ROUTE-SOURCE-ACCESS-TOKEN";

    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var credential = new CredentialRecord
        {
            Name = "mcp-credential",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        // The fake serves at {base}/mcp, so that is the destination; the route contributes only
        // the prefix the funnel dials.
        var upstreamRecord = new UpstreamRecord { Name = "mcp-upstream", BaseUrl = _upstream.Url };

        var route = new RouteMapping
        {
            // Not "/mcp": that prefix is reserved for the funnel's own endpoints, and
            // RouteValidation refuses it. "/mcpsrv" is a different segment and stays allowed.
            PathPrefix = "/mcpsrv",
            UpstreamId = upstreamRecord.Id,
            Credentials = [RouteCredential.For(credential.Id, CredentialPlacement.Header)],
            StripPrefix = true,
        };

        await _host.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstreamRecord);
            store.Routes.Add(route);
        });
        _host.RebuildProxyConfig();

        var source = await _host.AddRouteSourceAsync("auth", route.Id);
        await _host.AddFunnelAsync("agent", new McpFunnelSource { SourceId = source.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    [Fact]
    public async Task TheUpstreamReceivesTheRoutesOAuthToken()
    {
        var client = await _host.ConnectAsync("agent");

        Assert.Contains("auth__echo", (await client.ListToolsAsync()).Select(t => t.Name));
        Assert.Equal("through-the-route", await FunnelTestHost.CallTextAsync(client, "auth__echo", "through-the-route"));

        Assert.NotEmpty(_upstream.ReceivedAuthorization);
        Assert.All(_upstream.ReceivedAuthorization, header => Assert.Equal($"Bearer {Token}", header));
    }

    [Fact]
    public async Task TheProxysOwnHeadersDoNotReachTheUpstream()
    {
        // The local API key authenticates the funnel to this proxy, and the hop marker is purely
        // internal. Neither has any business in an upstream's access log.
        var client = await _host.ConnectAsync("agent");
        await client.ListToolsAsync();

        Assert.NotEmpty(_upstream.ReceivedHeaders);
        Assert.All(_upstream.ReceivedHeaders, headers =>
        {
            Assert.False(headers.ContainsKey("X-Proxy-Key"));
            Assert.False(headers.ContainsKey("X-Proxy-Funnel-Hop"));
        });
    }

    [Fact]
    public async Task TheHopIntoTheRouteUsesTheRoutesOwnKey_NotTheFunnels()
    {
        // Keys are per endpoint, so the funnel's key is not accepted by the route it pools. The
        // hop has to present the route's, and it reads it live — a key changed on the Routes tab
        // must not leave every funnel over that route authenticating with a stale one.
        var route = _host.Cache.Current.Routes.Single();
        await _host.MutateAsync(_ => route.Key.Value = "a-key-only-this-route-accepts");

        // The pooled session was opened with the previous key; drop it so the next call
        // re-dials, exactly as editing the route in the GUI does.
        await _host.Pool.InvalidateAllAsync();

        var client = await _host.ConnectAsync("agent");

        Assert.Equal("still-authorized", await FunnelTestHost.CallTextAsync(client, "auth__echo", "still-authorized"));
    }

    [Fact]
    public async Task ARouteSourceStillGetsItsOwnSessionPerFunnel()
    {
        // Same isolation guarantee as a direct URL source — the credentialed hop must not
        // collapse two endpoints onto one upstream session.
        await _host.AddFunnelAsync("second",
            new McpFunnelSource { SourceId = _host.Cache.Current.McpSources.Single().Id });

        var first = await _host.ConnectAsync("agent");
        var second = await _host.ConnectAsync("second");

        var sessionOne = await FunnelTestHost.CallTextAsync(first, "auth__whoami");
        var sessionTwo = await FunnelTestHost.CallTextAsync(second, "auth__whoami");

        Assert.NotEqual(sessionOne, sessionTwo);
    }

    [Fact]
    public async Task ManyFunnelsOverOneRouteProxiedServerRunSimultaneously()
    {
        // The full stack under concurrent load: several agents, each on its own endpoint, all
        // reaching one credentialed MCP server through the same route at the same moment. Every
        // hop in between — the funnel, the local guard, YARP, the credential transform — is
        // exercised by every call.
        var sourceId = _host.Cache.Current.McpSources.Single().Id;

        string[] slugs = ["agent", "alpha", "beta", "gamma"];
        foreach (var slug in slugs.Skip(1))
        {
            await _host.AddFunnelAsync(slug, new McpFunnelSource { SourceId = sourceId });
        }

        var clients = new List<(string Slug, McpClient Client)>();
        foreach (var slug in slugs)
        {
            clients.Add((slug, await _host.ConnectAsync(slug)));
        }

        const int callsPerFunnel = 30;

        var work = new List<Task<(string Expected, string Actual)>>();
        foreach (var (slug, client) in clients)
        {
            for (var i = 0; i < callsPerFunnel; i++)
            {
                var payload = $"{slug}-{i}";
                work.Add(EchoAsync(client, payload));
            }
        }

        var results = await Task.WhenAll(work);

        // Nothing crossed wires: every response is the payload its own caller sent.
        Assert.Equal(slugs.Length * callsPerFunnel, results.Length);
        foreach (var (expected, actual) in results)
        {
            Assert.Equal(expected, actual);
        }

        // One upstream session per endpoint, not one shared by all four and not one per call.
        var sessions = new List<string>();
        foreach (var (_, client) in clients)
        {
            sessions.Add(await FunnelTestHost.CallTextAsync(client, "auth__whoami"));
        }

        Assert.Equal(slugs.Length, sessions.Distinct().Count());

        // And every one of those calls carried the route's OAuth token.
        Assert.All(_upstream.ReceivedAuthorization, header => Assert.Equal($"Bearer {Token}", header));

        static async Task<(string, string)> EchoAsync(McpClient client, string payload) =>
            (payload, await FunnelTestHost.CallTextAsync(client, "auth__echo", payload));
    }

    [Fact]
    public async Task TwoFunnelsOverTheSameRouteAreInFlightTogether()
    {
        // Proves the credentialed hop is not a bottleneck. The upstream holds both calls open
        // until released; a pipeline that serialized them — one shared session, or a lock around
        // the route — would never get the second one in flight and this would time out.
        var sourceId = _host.Cache.Current.McpSources.Single().Id;
        await _host.AddFunnelAsync("second", new McpFunnelSource { SourceId = sourceId });

        _upstream.ResetSlowGate();

        var first = await _host.ConnectAsync("agent");
        var second = await _host.ConnectAsync("second");

        var callOne = FunnelTestHost.CallTextAsync(first, "auth__slow", "one");
        var callTwo = FunnelTestHost.CallTextAsync(second, "auth__slow", "two");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (_upstream.SlowCallsInFlight.Count < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(_upstream.SlowCallsInFlight.Count >= 2,
            "both funnels' calls should reach the upstream before either completes");

        _upstream.SlowToolGate.SetResult();

        Assert.Equal("one", await callOne.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("two", await callTwo.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task FunnelsOverTheSameRouteKeepTheirOwnToolSets()
    {
        // Two agents, one credentialed upstream, different permissions — the reason for pooling
        // an authenticated server behind several funnels rather than handing out the route.
        var sourceId = _host.Cache.Current.McpSources.Single().Id;

        await _host.AddFunnelAsync("narrow", new McpFunnelSource
        {
            SourceId = sourceId,
            ToolMode = McpSelectionMode.Include,
            Tools = ["echo"],
        });

        var wide = await _host.ConnectAsync("agent");
        var narrow = await _host.ConnectAsync("narrow");

        var wideList = wide.ListToolsAsync();
        var narrowList = narrow.ListToolsAsync();

        Assert.Contains("auth__alpha", (await wideList).Select(t => t.Name));
        Assert.Equal(["auth__echo"], (await narrowList).Select(t => t.Name).ToList());

        // And the restriction holds on the call path, not just the listing.
        var refused = await FunnelTestHost.CallAsync(narrow, "auth__alpha");
        Assert.True(refused.IsError);
        Assert.Equal(0, _upstream.CallsByTool.GetValueOrDefault("alpha"));
    }
}
