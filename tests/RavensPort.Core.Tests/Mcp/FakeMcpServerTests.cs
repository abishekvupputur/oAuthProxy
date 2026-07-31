using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// Proves the test double is itself a working MCP server, directly, with no funnel in the way.
///
/// Without this, every funnel test has two suspects for the same symptom — an empty tool list
/// could mean the funnel dropped it or that the fake never offered it — and the failure gives no
/// hint which. This pins the fake so the isolation suite's failures can only be about the funnel.
/// </summary>
public class FakeMcpServerTests : IAsyncLifetime
{
    private FakeMcpServer _server = null!;

    public async Task InitializeAsync() => _server = await FakeMcpServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<McpClient> ConnectAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_server.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
        });

        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task Connects_AndReportsItsTools()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();

        Assert.Contains("echo", tools.Select(t => t.Name));
        Assert.Equal(1, _server.InitializeCount);
    }

    [Fact]
    public async Task EchoesBackTheArgumentItWasGiven()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["value"] = "round-trip" });

        Assert.Equal("round-trip", result.Content.OfType<TextContentBlock>().Single().Text);
    }

    [Fact]
    public async Task ReportsTheSessionEachCallWasServedUnder()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();

        var sessionOne = await CallWhoAmIAsync(first);
        var sessionTwo = await CallWhoAmIAsync(second);

        Assert.NotEqual("", sessionOne);
        Assert.NotEqual("none", sessionOne);
        Assert.NotEqual(sessionOne, sessionTwo);
        Assert.Equal(sessionOne, await CallWhoAmIAsync(first));

        static async Task<string> CallWhoAmIAsync(McpClient client)
        {
            var result = await client.CallToolAsync("whoami", new Dictionary<string, object?>());
            return result.Content.OfType<TextContentBlock>().Single().Text;
        }
    }

    [Fact]
    public async Task ListsResourcesAndPrompts()
    {
        await using var client = await ConnectAsync();

        Assert.Contains("mem://doc/one", (await client.ListResourcesAsync()).Select(r => r.Uri));
        Assert.Contains("greeting", (await client.ListPromptsAsync()).Select(p => p.Name));
    }
}
