using OAuthProxy.Core.Mcp;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Tests.Mcp;

/// <summary>
/// Discovery is what fills the tick lists in the GUI, and its failure mode used to be the worst
/// kind: silent. Every exception inside the per-capability listing was swallowed and turned into
/// an empty catalog with no error, so a source that could not be reached at all was reported as
/// "connected — nothing offered". The user's question then becomes "why are there no tools?" with
/// nothing anywhere to answer it.
///
/// These tests hold the line between the two cases that must never look alike: a server that
/// genuinely offers nothing, and a server that could not be reached.
/// </summary>
public class McpSourceDiscoveryTests : IAsyncLifetime
{
    private FakeMcpServer _server = null!;
    private FunnelTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _server = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task AHealthyServerReportsEverythingItOffers()
    {
        var source = await _host.AddRemoteSourceAsync("up", _server.Url);

        var catalog = await _host.Pool.DiscoverAsync(source);

        Assert.Null(catalog.Error);
        Assert.Contains("echo", catalog.Tools);
        Assert.Contains("mem://doc/one", catalog.Resources);
        Assert.Contains("greeting", catalog.Prompts);
        Assert.Contains("tools", catalog.Describe());
    }

    [Fact]
    public async Task AnUnreachableServerReportsAnError_NotAnEmptyCatalog()
    {
        // The regression this suite exists for. Before, this returned Error = null and read as a
        // successful connection to a server with nothing to offer.
        var source = await _host.AddRemoteSourceAsync("dead", "https://127.0.0.1:1/mcp");

        var catalog = await _host.Pool.DiscoverAsync(source);

        Assert.NotNull(catalog.Error);
        Assert.True(catalog.IsEmpty);
        Assert.DoesNotContain("nothing offered", catalog.Describe());
    }

    [Fact]
    public async Task AServerThatIsDownReportsAnError()
    {
        var source = await _host.AddRemoteSourceAsync("up", _server.Url);
        _server.IsDown = true;

        var catalog = await _host.Pool.DiscoverAsync(source);

        Assert.NotNull(catalog.Error);
    }

    [Fact]
    public async Task AServerOfferingOnlyToolsIsNotReportedAsBroken()
    {
        // The reason discovery cannot simply treat every failure as fatal: most real servers
        // implement tools and answer "method not found" for prompts and resources.
        var source = await _host.AddRemoteSourceAsync("up", _server.Url);
        _server.SupportsPromptsAndResources = false;

        var catalog = await _host.Pool.DiscoverAsync(source);

        Assert.Null(catalog.Error);
        Assert.NotEmpty(catalog.Tools);
        Assert.Empty(catalog.Resources);
        Assert.Empty(catalog.Prompts);
    }

    [Fact]
    public async Task AnUnsupportedCapabilityDoesNotCostTheSession()
    {
        // "Method not found: resources/list" is an answer, not a disconnection. Treating it as a
        // dead session dropped a working connection and re-handshook on every discovery pass —
        // visible in the activity log as an endless connect/reconnect churn against servers that
        // were behaving perfectly.
        var source = await _host.AddRemoteSourceAsync("up", _server.Url);
        _server.SupportsPromptsAndResources = false;

        await _host.Pool.DiscoverAsync(source);
        await _host.Pool.DiscoverAsync(source);

        // One handshake for the first connect; the unsupported capabilities must not have forced
        // any more, and the second pass reuses the same session.
        Assert.Equal(1, _server.InitializeCount);

        Assert.DoesNotContain(
            _host.ActivityLog.GetRecent(100),
            line => line.Contains("session was stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARouteBackedSourceWhoseRouteIsGoneReportsAnError()
    {
        // Deleting the route out from under a source leaves it unreachable. That has to show up
        // on the source's row rather than as an empty tool list.
        var source = await _host.AddRouteSourceAsync("orphan", Guid.NewGuid());

        var catalog = await _host.Pool.DiscoverAsync(source);

        Assert.NotNull(catalog.Error);
        Assert.Contains("route", catalog.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoveryRecoversOnceTheServerComesBack()
    {
        // A failed handshake must not be cached, or a source stays broken until restart even
        // after whatever caused it is fixed.
        var source = await _host.AddRemoteSourceAsync("up", _server.Url);

        _server.IsDown = true;
        Assert.NotNull((await _host.Pool.DiscoverAsync(source)).Error);

        _server.IsDown = false;
        var recovered = await _host.Pool.DiscoverAsync(source);

        Assert.Null(recovered.Error);
        Assert.NotEmpty(recovered.Tools);
    }

    [Fact]
    public async Task AFailedDiscoveryIsWrittenToTheActivityLog()
    {
        var source = await _host.AddRemoteSourceAsync("dead", "https://127.0.0.1:1/mcp");

        await _host.Pool.DiscoverAsync(source);

        Assert.Contains(
            _host.ActivityLog.GetRecent(50),
            line => line.Contains("could not be reached", StringComparison.Ordinal));
    }
}
