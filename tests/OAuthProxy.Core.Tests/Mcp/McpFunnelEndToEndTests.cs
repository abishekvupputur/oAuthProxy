using System.Net;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OAuthProxy.Core.Mcp;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;

namespace OAuthProxy.Core.Tests.Mcp;

/// <summary>
/// The funnel as an agent actually meets it: a real MCP client, over real HTTP, against the real
/// pipeline. Covers what the endpoint exposes (prefixed tools, rewritten resource URIs, prompts)
/// and what it refuses (no key, feature off, unknown slug, a request that already came through a
/// funnel).
/// </summary>
public class McpFunnelEndToEndTests : IAsyncLifetime
{
    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;
    private McpSourceRecord _source = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        _source = await _host.AddRemoteSourceAsync("up", _upstream.Url);
        await _host.AddFunnelAsync("agent", new McpFunnelSource { SourceId = _source.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    [Fact]
    public async Task ToolsArePrefixedWithTheSourceAlias_AndCallable()
    {
        var client = await _host.ConnectAsync("agent");

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.All(tools, name => Assert.StartsWith("up__", name));
        Assert.Contains("up__echo", tools);

        Assert.Equal("hello", await FunnelTestHost.CallTextAsync(client, "up__echo", "hello"));
    }

    [Fact]
    public async Task AnUnprefixedToolName_IsRefused()
    {
        // The upstream's own name must not work: the prefix is the only thing identifying which
        // source a call belongs to, so a bare name has no destination.
        var client = await _host.ConnectAsync("agent");

        var result = await FunnelTestHost.CallAsync(client, "echo", "hello");

        Assert.True(result.IsError);
        Assert.Equal(0, _upstream.CallsByTool.GetValueOrDefault("echo"));
    }

    [Fact]
    public async Task AToolFromAnUnknownAlias_IsRefused()
    {
        var client = await _host.ConnectAsync("agent");

        var result = await FunnelTestHost.CallAsync(client, "nosuch__echo", "hello");

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ResourcesAreRewrittenIntoFunnelUris_AndReadableBack()
    {
        var client = await _host.ConnectAsync("agent");

        var resources = await client.ListResourcesAsync();
        var first = resources.First(r => r.Uri.Contains("doc%2Fone") || r.Uri.Contains("doc/one"));

        Assert.StartsWith("funnel://up/", first.Uri);

        var read = await client.ReadResourceAsync(first.Uri);
        var contents = read.Contents.OfType<TextResourceContents>().Single();

        // The upstream saw its own URI, not the rewritten one.
        Assert.Contains("mem://doc/one", contents.Text);
    }

    [Fact]
    public async Task PromptsArePrefixed_AndRetrievable()
    {
        var client = await _host.ConnectAsync("agent");

        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).ToList();
        Assert.Contains("up__greeting", prompts);

        var prompt = await client.GetPromptAsync("up__greeting");
        var text = prompt.Messages.Select(m => m.Content).OfType<TextContentBlock>().Single().Text;

        Assert.Contains("greeting", text);
    }

    [Fact]
    public async Task ExcludedTools_DisappearFromTheEndpoint()
    {
        await _host.MutateAsync(store =>
        {
            var link = store.McpFunnels.Single(f => f.Slug == "agent").Sources.Single();
            link.ToolMode = McpSelectionMode.Exclude;
            link.Tools = ["alpha", "beta"];
        });

        var client = await _host.ConnectAsync("agent");
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.DoesNotContain("up__alpha", tools);
        Assert.DoesNotContain("up__beta", tools);
        Assert.Contains("up__echo", tools);
    }

    [Fact]
    public async Task WithoutTheLocalApiKey_TheEndpointIsForbidden()
    {
        using var client = new HttpClient();

        var response = await client.PostAsync(
            $"{_host.BaseUrl}/mcp/agent",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheFeatureIsOff_TheEndpointIsGone()
    {
        await _host.MutateAsync(store => store.Settings.McpFunnelEnabled = false);

        Assert.Equal(HttpStatusCode.NotFound, await ProbeAsync("/mcp/agent"));

        // And comes back without a restart.
        await _host.MutateAsync(store => store.Settings.McpFunnelEnabled = true);
        Assert.NotEqual(HttpStatusCode.NotFound, await ProbeAsync("/mcp/agent"));
    }

    [Fact]
    public async Task AnUnknownSlug_IsRefusedRatherThanServedAsAnEmptyServer()
    {
        // 403 rather than the gate's 404: a slug that names no funnel has no key to authenticate
        // against, so the guard turns it away before the gate is reached. Either way it must not
        // be answered as a working-but-empty MCP server, which is what an agent would otherwise
        // report as "the tools are gone" rather than "that endpoint does not exist".
        Assert.Equal(HttpStatusCode.Forbidden, await ProbeAsync("/mcp/no-such-agent"));
    }

    [Fact]
    public async Task ADisabledFunnel_IsNotServed()
    {
        await _host.MutateAsync(store => store.McpFunnels.Single(f => f.Slug == "agent").Enabled = false);

        Assert.Equal(HttpStatusCode.NotFound, await ProbeAsync("/mcp/agent"));
    }

    [Fact]
    public async Task ARequestThatAlreadyPassedThroughAFunnel_IsRefused()
    {
        // The loop guard. A funnel stamps this header on every request it makes to a source; if
        // such a request arrives back at a funnel endpoint, a source has been pointed at the
        // funnel itself and the two would recurse.
        using var client = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_host.BaseUrl}/mcp/agent")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);
        request.Headers.Add(LocalAccessGuard.FunnelHopHeaderName, "1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ADeletedSource_LeavesTheEndpointServingWhatRemains()
    {
        await _host.AddFunnelAsync("wide", new McpFunnelSource { SourceId = _source.Id });

        var client = await _host.ConnectAsync("wide");
        Assert.NotEmpty(await client.ListToolsAsync());

        await _host.MutateAsync(store => store.McpSources.RemoveAll(s => s.Id == _source.Id));

        // The funnel still answers — with nothing, rather than failing the whole request.
        Assert.Empty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task ADisabledSource_ContributesNothing()
    {
        await _host.MutateAsync(store => store.McpSources.Single(s => s.Id == _source.Id).Enabled = false);

        var client = await _host.ConnectAsync("agent");

        Assert.Empty(await client.ListToolsAsync());
    }

    private async Task<HttpStatusCode> ProbeAsync(string path)
    {
        using var client = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_host.BaseUrl}{path}")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
