using RavensPort.Core.Mcp;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// What the MCP Funnel tab's STATUS column is allowed to contain.
///
/// The text comes from a transport exception, and the MCP SDK builds those by appending the
/// upstream's response body to the message. An MCP endpoint is very often a web page as well — a
/// Google Apps Script deployment answers a GET with its whole HTML document — so without a cap the
/// column renders a web page inside one grid row.
/// </summary>
public class McpSourceCatalogStatusTests
{
    [Fact]
    public void AnHtmlResponseBodyDoesNotReachTheCell()
    {
        var page = "Response status code does not indicate success: 200 (OK). Response body: "
                   + "<!DOCTYPE html><html><head><style>.x{color:red}</style>"
                   + "<script>var a = 1; document.write('hello');</script></head>"
                   + "<body><p>Sign in to continue</p></body></html>";

        var status = McpSourceCatalog.Failed(page).Describe();

        Assert.DoesNotContain("<", status);
        Assert.DoesNotContain("document.write", status);
        Assert.DoesNotContain("color:red", status);
        Assert.Contains("Sign in to continue", status);
    }

    [Fact]
    public void TheCellStaysOneShortLine()
    {
        var status = McpSourceCatalog.Failed(new string('x', 5_000)).Describe();

        Assert.True(status.Length < 220, $"status was {status.Length} characters");
        Assert.EndsWith("…", status);
    }

    [Fact]
    public void NewlinesNeverSurvive()
    {
        // A wrapping cell turned every newline in a stack trace into another row of height.
        var status = McpSourceCatalog.Failed("first line\r\n\r\n   second line\n\tthird").Describe();

        Assert.Equal("⚠ first line second line third", status);
    }

    [Fact]
    public void APageWithNoProseStillSaysSomething()
    {
        var status = McpSourceCatalog.Failed("<html><head><script>var a=1;</script></head><body></body></html>").Describe();

        Assert.Contains("web page", status);
    }

    [Fact]
    public void AnOrdinaryFailureIsLeftAlone()
    {
        // The common case must not be mangled by any of the above.
        const string message = "The SSL connection could not be established, see inner exception.";

        Assert.Equal($"⚠ {message}", McpSourceCatalog.Failed(message).Describe());
    }

    [Fact]
    public void TheFullTextSurvivesOnTheTooltip()
    {
        // Trimming the cell must not lose the detail; it moves to the tooltip.
        var catalog = McpSourceCatalog.Failed(new string('x', 5_000));

        Assert.NotNull(catalog.Detail);
        Assert.Equal(5_000, catalog.Detail!.Length);
    }

    [Fact]
    public void ASuccessfulSourceHasNoTooltipAndCountsItsPrimitives()
    {
        var catalog = new McpSourceCatalog(["a", "b"], ["r"], [], DateTimeOffset.UtcNow, Error: null);

        Assert.Equal("2 tools · 1 resources", catalog.Describe());
        Assert.Null(catalog.Detail);
    }
}
