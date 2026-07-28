using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// A path prefix is interpolated into an ASP.NET route template, so some ordinary-looking
/// characters have structural meaning. An unparseable template makes YARP reject the whole
/// config update and keep the previous one, while the activity log has already announced the
/// route as active - one bad character used to make every later route edit appear to apply
/// and do nothing.
/// </summary>
public class RouteValidationTests
{
    [Theory]
    [InlineData("/gmail")]
    [InlineData("/app/echo")]
    [InlineData("/a-b_c.d~e")]
    [InlineData("/files/a..b")]   // two dots inside a segment is not a traversal
    [InlineData("/gmail/")]
    public void ValidatePathPrefix_AcceptsOrdinaryPrefixes(string prefix) =>
        Assert.Null(RouteValidation.ValidatePathPrefix(prefix));

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("?")]
    [InlineData("#")]
    [InlineData("\\")]
    public void ValidatePathPrefix_RejectsRouteTemplateMetacharacters(string character)
    {
        var error = RouteValidation.ValidatePathPrefix($"/api{character}x");

        Assert.NotNull(error);
        Assert.Contains("special meaning", error);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    public void ValidatePathPrefix_RejectsCatchAllPrefix(string prefix)
    {
        // "/{**catch-all}" swallows every request to the proxy and points all of it at one
        // upstream with one credential attached.
        var error = RouteValidation.ValidatePathPrefix(prefix);

        Assert.NotNull(error);
        Assert.Contains("every request", error);
    }

    [Theory]
    [InlineData("/api/../admin")]
    [InlineData("/../etc")]
    public void ValidatePathPrefix_RejectsDotSegments(string prefix) =>
        Assert.NotNull(RouteValidation.ValidatePathPrefix(prefix));

    [Fact]
    public void ValidatePathPrefix_RequiresALeadingSlash() =>
        Assert.NotNull(RouteValidation.ValidatePathPrefix("gmail"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePathPrefix_RejectsBlank(string? prefix) =>
        Assert.NotNull(RouteValidation.ValidatePathPrefix(prefix));

    [Fact]
    public void ValidatePathPrefix_RejectsSpacesAndControlCharacters()
    {
        Assert.NotNull(RouteValidation.ValidatePathPrefix("/two words"));
        Assert.NotNull(RouteValidation.ValidatePathPrefix("/tab\there"));
    }
}
