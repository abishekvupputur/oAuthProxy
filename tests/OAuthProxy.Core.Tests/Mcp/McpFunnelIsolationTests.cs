using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OAuthProxy.Core.Mcp;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Tests.Mcp;

/// <summary>
/// The behaviour the funnel exists to provide: each local endpoint has to act like a standalone
/// MCP server, even when several of them are fronting the same upstream at the same instant.
///
/// These are deliberately end-to-end — real MCP clients, real HTTP, the real pipeline — because
/// the failure modes being ruled out are not visible in unit-level pieces. A pool keyed by source
/// instead of by (funnel, source) still passes every filtering test; what it breaks is that two
/// endpoints stop being independent, and only a concurrent, two-endpoint test can see that.
/// </summary>
public class McpFunnelIsolationTests : IAsyncLifetime
{
    private FakeMcpServer _shared = null!;
    private FakeMcpServer _second = null!;
    private FunnelTestHost _host = null!;

    private McpSourceRecord _sharedSource = null!;
    private McpSourceRecord _secondSource = null!;

    public async Task InitializeAsync()
    {
        _shared = await FakeMcpServer.StartAsync();
        _second = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        _sharedSource = await _host.AddRemoteSourceAsync("sh", _shared.Url);
        _secondSource = await _host.AddRemoteSourceAsync("sc", _second.Url);

        // "one" sees everything the shared source offers; "two" sees two tools of it. Both point
        // at the *same* upstream, which is the case that per-source session pooling would break.
        await _host.AddFunnelAsync("one", new McpFunnelSource { SourceId = _sharedSource.Id });
        await _host.AddFunnelAsync("two", new McpFunnelSource
        {
            SourceId = _sharedSource.Id,
            ToolMode = McpSelectionMode.Include,
            Tools = ["echo", "whoami"],
        });

        // A second unrestricted view of the same upstream. The parallelism tests need two
        // endpoints that both expose "slow"; "two" deliberately does not.
        await _host.AddFunnelAsync("mirror", new McpFunnelSource { SourceId = _sharedSource.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _shared.DisposeAsync();
        await _second.DisposeAsync();
    }

    [Fact]
    public async Task EachEndpoint_HoldsItsOwnUpstreamSession()
    {
        var one = await _host.ConnectAsync("one");
        var two = await _host.ConnectAsync("two");

        var sessionOne = await FunnelTestHost.CallTextAsync(one, "sh__whoami");
        var sessionTwo = await FunnelTestHost.CallTextAsync(two, "sh__whoami");

        Assert.NotEqual("", sessionOne);
        Assert.NotEqual(sessionOne, sessionTwo);
    }

    [Fact]
    public async Task OneEndpoint_ReusesTheSameUpstreamSessionAcrossCalls()
    {
        // The other half of the previous test: sessions are per endpoint, not per request. A pool
        // that reconnected every call would also produce "different" ids above, for the wrong
        // reason, and would pay a handshake on every tool invocation.
        var one = await _host.ConnectAsync("one");

        var first = await FunnelTestHost.CallTextAsync(one, "sh__whoami");
        var second = await FunnelTestHost.CallTextAsync(one, "sh__whoami");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ReconnectingOneEndpoint_LeavesTheOthersSessionAlone()
    {
        var one = await _host.ConnectAsync("one");
        var two = await _host.ConnectAsync("two");

        var sessionTwoBefore = await FunnelTestHost.CallTextAsync(two, "sh__whoami");

        // Drop everything funnel "one" holds, as an edit to that funnel would.
        await _host.Pool.InvalidateFunnelAsync(
            _host.Cache.Current.McpFunnels.Single(f => f.Slug == "one").Id);

        var sessionOneAfter = await FunnelTestHost.CallTextAsync(one, "sh__whoami");
        var sessionTwoAfter = await FunnelTestHost.CallTextAsync(two, "sh__whoami");

        Assert.Equal(sessionTwoBefore, sessionTwoAfter);
        Assert.NotEqual(sessionTwoAfter, sessionOneAfter);
    }

    [Fact]
    public async Task ConcurrentCallsAcrossEndpoints_EachResponseReachesItsOwnCaller()
    {
        // The cross-delivery test. Every call carries a payload unique to it; getting somebody
        // else's payload back means responses are being matched to the wrong request somewhere
        // between the two endpoints and the shared upstream.
        var one = await _host.ConnectAsync("one");
        var two = await _host.ConnectAsync("two");

        const int callsPerEndpoint = 100;

        var work = new List<Task<(string Expected, string Actual)>>();
        for (var i = 0; i < callsPerEndpoint; i++)
        {
            work.Add(CallAsync(one, $"one-{i}"));
            work.Add(CallAsync(two, $"two-{i}"));
        }

        var results = await Task.WhenAll(work);

        Assert.Equal(callsPerEndpoint * 2, results.Length);
        foreach (var (expected, actual) in results)
        {
            Assert.Equal(expected, actual);
        }

        static async Task<(string, string)> CallAsync(McpClient client, string payload) =>
            (payload, await FunnelTestHost.CallTextAsync(client, "sh__echo", payload));
    }

    [Fact]
    public async Task CallsOnDifferentEndpoints_AreInFlightTogether()
    {
        // Proves parallelism rather than merely correctness. The upstream holds every "slow" call
        // open until the gate is released, so if the funnel serialized calls — one shared session
        // with a lock, or an accidental await chain — the first call would never return and the
        // gate would never be released. The test would then fail by timeout, which is exactly the
        // signal wanted.
        _shared.ResetSlowGate();

        var one = await _host.ConnectAsync("one");
        var mirror = await _host.ConnectAsync("mirror");

        var callOne = FunnelTestHost.CallTextAsync(one, "sh__slow", "from-one");
        var callTwo = FunnelTestHost.CallTextAsync(mirror, "sh__slow", "from-mirror");

        // Both must reach the upstream before either is allowed to finish.
        await WaitForAsync(() => _shared.SlowCallsInFlight.Count >= 2, TimeSpan.FromSeconds(20));

        _shared.SlowToolGate.SetResult();

        Assert.Equal("from-one", await callOne.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("from-mirror", await callTwo.WaitAsync(TimeSpan.FromSeconds(20)));

        // Two endpoints, two upstream sessions — not one shared connection serving both.
        Assert.Equal(2, _shared.CallsPerSession.Count);
    }

    [Fact]
    public async Task ConcurrentCallsOnOneEndpoint_AreAlsoInFlightTogether()
    {
        // Same guarantee within a single endpoint: one session multiplexes concurrent requests,
        // so a busy agent is not stuck behind its own slowest tool call.
        _shared.ResetSlowGate();

        var one = await _host.ConnectAsync("one");

        var first = FunnelTestHost.CallTextAsync(one, "sh__slow", "a");
        var second = FunnelTestHost.CallTextAsync(one, "sh__slow", "b");

        await WaitForAsync(() => _shared.SlowCallsInFlight.Count >= 2, TimeSpan.FromSeconds(20));

        _shared.SlowToolGate.SetResult();

        Assert.Equal("a", await first.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("b", await second.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task EndpointsExposeDifferentToolSets_AtTheSameMoment()
    {
        var one = await _host.ConnectAsync("one");
        var two = await _host.ConnectAsync("two");

        var listOne = one.ListToolsAsync();
        var listTwo = two.ListToolsAsync();

        var toolsOne = (await listOne).Select(t => t.Name).ToList();
        var toolsTwo = (await listTwo).Select(t => t.Name).ToList();

        Assert.Contains("sh__alpha", toolsOne);
        Assert.DoesNotContain("sh__alpha", toolsTwo);
        Assert.Equal(["sh__echo", "sh__whoami"], toolsTwo.Order().ToList());
    }

    [Fact]
    public async Task FilteringHolds_WhenBothEndpointsAreHammeredAtOnce()
    {
        // A filtered-out tool has to be refused on the call path too. An agent that saw the name
        // before it was unticked — or that simply guessed it — must not get through.
        var one = await _host.ConnectAsync("one");
        var two = await _host.ConnectAsync("two");

        const int attempts = 25;

        var allowed = new List<Task<string>>();
        var refused = new List<Task<CallToolResult>>();

        for (var i = 0; i < attempts; i++)
        {
            allowed.Add(FunnelTestHost.CallTextAsync(one, "sh__alpha"));
            refused.Add(FunnelTestHost.CallAsync(two, "sh__alpha"));
        }

        foreach (var result in await Task.WhenAll(allowed))
        {
            Assert.Equal("called alpha", result);
        }

        // A refused call comes back as an error result rather than an exception — the SDK turns
        // the handler's rejection into one — so IsError is what has to be asserted.
        foreach (var result in await Task.WhenAll(refused))
        {
            Assert.True(result.IsError);
        }

        // The decisive assertion: the upstream saw only the permitted endpoint's calls. Filtering
        // that merely hid the tool from tools/list, while still forwarding calls to it, would
        // pass every check above and fail this one.
        Assert.Equal(attempts, _shared.CallsByTool["alpha"]);
    }

    [Fact]
    public async Task ADeadSource_DegradesOnlyItself()
    {
        // "wide" pools a healthy source and one that is about to fall over. Losing the second
        // must not blank the agent's whole toolset, and must not touch the other funnel at all.
        await _host.AddFunnelAsync("wide",
            new McpFunnelSource { SourceId = _sharedSource.Id },
            new McpFunnelSource { SourceId = _secondSource.Id });

        var wide = await _host.ConnectAsync("wide");
        var one = await _host.ConnectAsync("one");

        var before = (await wide.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("sh__echo", before);
        Assert.Contains("sc__echo", before);

        _second.IsDown = true;

        var after = (await wide.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("sh__echo", after);
        Assert.DoesNotContain("sc__echo", after);

        // The healthy source still answers calls, on both funnels.
        Assert.Equal("still-here", await FunnelTestHost.CallTextAsync(wide, "sh__echo", "still-here"));
        Assert.Equal("unaffected", await FunnelTestHost.CallTextAsync(one, "sh__echo", "unaffected"));

        // And it recovers without anyone restarting anything.
        _second.IsDown = false;
        var recovered = (await wide.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("sc__echo", recovered);
    }

    [Fact]
    public async Task AnExpiredUpstreamSession_IsRebuiltForIdempotentCalls()
    {
        var one = await _host.ConnectAsync("one");
        await FunnelTestHost.CallTextAsync(one, "sh__whoami");

        var handshakesBefore = _shared.InitializeCount;

        // Every held session is now stale; the upstream answers 404 to them, as a real server
        // does after its idle timeout or a restart.
        _shared.ExpireAllSessions();

        var tools = await one.ListToolsAsync();

        Assert.NotEmpty(tools);
        Assert.True(_shared.InitializeCount > handshakesBefore,
            "the stale session should have been replaced by a fresh handshake");
    }

    [Fact]
    public async Task Discovery_DoesNotDisturbALiveEndpointsSession()
    {
        // The GUI's Refresh button runs through the pool too. It must not borrow — or, on
        // failure, tear down — a session an agent is in the middle of using.
        var one = await _host.ConnectAsync("one");
        var sessionBefore = await FunnelTestHost.CallTextAsync(one, "sh__whoami");

        await _host.Pool.ExecuteAsync(
            McpSourceConnectionPool.DiscoveryFunnelId,
            _sharedSource,
            (client, ct) => client.ListToolsAsync(cancellationToken: ct),
            isIdempotent: true);

        var sessionAfter = await FunnelTestHost.CallTextAsync(one, "sh__whoami");

        Assert.Equal(sessionBefore, sessionAfter);
    }

    [Fact]
    public async Task AnEditToOneFunnel_TakesEffectWithoutTouchingTheOther()
    {
        // Stateless mode is what buys this: options are rebuilt per request, so an unticked tool
        // disappears on the agent's next list with no reconnect and no notification.
        var one = await _host.ConnectAsync("one");
        var two = await _host.ConnectAsync("two");

        Assert.Contains("sh__alpha", (await one.ListToolsAsync()).Select(t => t.Name));

        await _host.MutateAsync(store =>
        {
            var funnel = store.McpFunnels.Single(f => f.Slug == "one");
            var link = funnel.Sources.Single();
            link.ToolMode = McpSelectionMode.Exclude;
            link.Tools = ["alpha"];
        });

        Assert.DoesNotContain("sh__alpha", (await one.ListToolsAsync()).Select(t => t.Name));
        Assert.Equal(["sh__echo", "sh__whoami"], (await two.ListToolsAsync()).Select(t => t.Name).Order().ToList());
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.Fail($"Condition was not met within {timeout.TotalSeconds:0}s.");
    }
}
