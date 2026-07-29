using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Tests.Mcp;

/// <summary>
/// The selection rules on their own. Include and Exclude differ in more than sign: Include is a
/// closed list, so a tool the upstream adds later stays hidden until someone picks it, while
/// Exclude is open and will surface it immediately. That difference is the whole reason both
/// exist, and it is easy to break without noticing.
/// </summary>
public class McpFunnelSelectionTests
{
    [Fact]
    public void AllMeansEverything_IncludingWhateverAppearsLater()
    {
        var link = new McpFunnelSource();

        Assert.True(link.AllowsTool("anything"));
        Assert.True(link.AllowsTool("added_next_week"));
    }

    [Fact]
    public void IncludeIsAClosedList()
    {
        var link = new McpFunnelSource { ToolMode = McpSelectionMode.Include, Tools = ["a", "b"] };

        Assert.True(link.AllowsTool("a"));
        Assert.True(link.AllowsTool("b"));
        Assert.False(link.AllowsTool("c"));

        // The safety property: a new upstream tool is not silently handed to the agent.
        Assert.False(link.AllowsTool("newly_added_upstream_tool"));
    }

    [Fact]
    public void ExcludeIsAnOpenList()
    {
        var link = new McpFunnelSource { ToolMode = McpSelectionMode.Exclude, Tools = ["dangerous"] };

        Assert.False(link.AllowsTool("dangerous"));
        Assert.True(link.AllowsTool("safe"));
        Assert.True(link.AllowsTool("newly_added_upstream_tool"));
    }

    [Fact]
    public void AnEmptyIncludeListExposesNothing()
    {
        // Not the same as All. Someone who switched to Include and picked nothing gets nothing,
        // which is the conservative reading and the one that matches the UI.
        var link = new McpFunnelSource { ToolMode = McpSelectionMode.Include, Tools = [] };

        Assert.False(link.AllowsTool("a"));
    }

    [Fact]
    public void MatchingIsExact_NotCaseInsensitiveOrPartial()
    {
        // Tool names are opaque identifiers to an upstream; treating "Read" and "read" as one
        // would let an Exclude entry fail to block the tool it named.
        var link = new McpFunnelSource { ToolMode = McpSelectionMode.Include, Tools = ["read"] };

        Assert.True(link.AllowsTool("read"));
        Assert.False(link.AllowsTool("Read"));
        Assert.False(link.AllowsTool("read_file"));
    }

    [Fact]
    public void EachPrimitiveKindIsSelectedIndependently()
    {
        var link = new McpFunnelSource
        {
            ToolMode = McpSelectionMode.Include,
            Tools = ["a"],
            ResourceMode = McpSelectionMode.Exclude,
            Resources = ["mem://blocked"],
            PromptMode = McpSelectionMode.All,
        };

        Assert.True(link.AllowsTool("a"));
        Assert.False(link.AllowsTool("b"));

        Assert.False(link.AllowsResource("mem://blocked"));
        Assert.True(link.AllowsResource("mem://other"));

        Assert.True(link.AllowsPrompt("anything"));
    }
}
