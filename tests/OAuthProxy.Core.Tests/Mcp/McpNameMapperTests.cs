using OAuthProxy.Core.Mcp;

namespace OAuthProxy.Core.Tests.Mcp;

/// <summary>
/// The prefix is not decoration — it is the funnel's entire routing table. A tools/call arrives
/// carrying nothing but the exposed name, so anything this class gets wrong sends an agent's call
/// to the wrong upstream or nowhere.
/// </summary>
public class McpNameMapperTests
{
    [Theory]
    [InlineData("gh", "create_issue", "gh__create_issue")]
    [InlineData("a", "b", "a__b")]
    [InlineData("my-source", "search", "my-source__search")]
    public void EncodesAliasAndName(string alias, string name, string expected) =>
        Assert.Equal(expected, McpNameMapper.Encode(alias, name));

    [Fact]
    public void RoundTripsThroughDecode()
    {
        var encoded = McpNameMapper.Encode("gh", "create_issue");

        Assert.True(McpNameMapper.TryDecode(encoded, out var alias, out var name));
        Assert.Equal("gh", alias);
        Assert.Equal("create_issue", name);
    }

    [Fact]
    public void SplitsOnTheFirstSeparatorSoUpstreamNamesMayContainOne()
    {
        // Real tools do contain double underscores. Splitting on the last one would hand the
        // upstream a truncated name and route by an alias that was never registered.
        Assert.True(McpNameMapper.TryDecode("gh__weird__tool__name", out var alias, out var name));
        Assert.Equal("gh", alias);
        Assert.Equal("weird__tool__name", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bare_name")]
    [InlineData("__leading")]
    [InlineData("trailing__")]
    public void RefusesNamesWithNoUsableSplit(string exposed) =>
        Assert.False(McpNameMapper.TryDecode(exposed, out _, out _));

    [Fact]
    public void TruncatesToTheProtocolLimit_KeepingTheAliasWhole()
    {
        var longName = new string('x', 200);

        var encoded = McpNameMapper.Encode("src", longName);

        Assert.Equal(McpNameMapper.MaxNameLength, encoded.Length);
        Assert.StartsWith("src__", encoded);

        // The alias still decodes, which is what keeps a truncated name from being routed to the
        // wrong source rather than simply refused.
        Assert.True(McpNameMapper.TryDecode(encoded, out var alias, out _));
        Assert.Equal("src", alias);
    }

    [Fact]
    public void ReportsWhetherANameHadToBeTruncated()
    {
        Assert.False(McpNameMapper.IsTruncated("src", "short"));
        Assert.True(McpNameMapper.IsTruncated("src", new string('x', 200)));

        // Exactly at the limit is not truncation.
        var exact = new string('x', McpNameMapper.MaxNameLength - "src__".Length);
        Assert.False(McpNameMapper.IsTruncated("src", exact));
    }

    [Theory]
    [InlineData("mem://doc/one")]
    [InlineData("https://example.com/a/b?c=d&e=f")]
    [InlineData("file:///C:/path with spaces/x.txt")]
    [InlineData("weird://has#hash/and?query")]
    public void ResourceUrisRoundTrip(string upstreamUri)
    {
        var encoded = McpNameMapper.EncodeResourceUri("src", upstreamUri);

        Assert.StartsWith("funnel://src/", encoded);
        Assert.True(McpNameMapper.TryDecodeResourceUri(encoded, out var alias, out var decoded));
        Assert.Equal("src", alias);
        Assert.Equal(upstreamUri, decoded);
    }

    [Fact]
    public void ResourceUriEncodingHidesTheInnerSchemesSlashes()
    {
        // The point of escaping rather than embedding verbatim: a client that normalizes URIs
        // would otherwise collapse "///" in the inner scheme before sending the read back, and
        // the upstream would be asked for a path it never offered.
        var encoded = McpNameMapper.EncodeResourceUri("src", "file:///etc/hosts");

        Assert.DoesNotContain("///", encoded[("funnel://src/".Length)..]);
    }

    [Fact]
    public void ResourceTemplatePlaceholdersSurviveEncoding()
    {
        // Literals are escaped, placeholders are not — a client has to be able to expand them.
        var encoded = McpNameMapper.EncodeResourceUriTemplate("src", "mem://doc/{id}");

        Assert.Contains("{id}", encoded);
        Assert.True(McpNameMapper.TryDecodeResourceUri(encoded, out var alias, out var decoded));
        Assert.Equal("src", alias);
        Assert.Equal("mem://doc/{id}", decoded);
    }

    [Fact]
    public void UnbalancedBracesInATemplateAreTreatedAsLiteral()
    {
        var encoded = McpNameMapper.EncodeResourceUriTemplate("src", "mem://doc/{unclosed");

        Assert.True(McpNameMapper.TryDecodeResourceUri(encoded, out _, out var decoded));
        Assert.Equal("mem://doc/{unclosed", decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mem://doc/one")]
    [InlineData("funnel://")]
    [InlineData("funnel://alias-only")]
    public void RefusesUrisThatAreNotFunnelUris(string uri) =>
        Assert.False(McpNameMapper.TryDecodeResourceUri(uri, out _, out _));
}
