namespace OAuthProxy.Core.Models;

public sealed class AppSettings
{
    public int ListenPort { get; set; } = 5559;
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Master switch for the MCP funnel endpoints under /mcp. Off by default: the funnel lets a
    /// caller reach several upstreams (and their attached OAuth grants) through one path, so it
    /// stays dark until the user asks for it rather than appearing on upgrade.
    /// </summary>
    public bool McpFunnelEnabled { get; set; }

    // There is deliberately no proxy-wide API key here any more. Authentication is per endpoint:
    // every route and every funnel carries its own <see cref="ProxyKey"/>, so a key leaked from
    // one client cannot spend the OAuth grant attached to a different route. A store written by
    // an older build still has "LocalApiKey" in its JSON; it is ignored on load, and every route
    // and funnel is issued a fresh key by ConfigStoreCache instead. Existing clients have to be
    // handed the key of the specific endpoint they call.
}
