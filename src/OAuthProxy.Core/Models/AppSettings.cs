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

    // There is deliberately no proxy-wide API key here. Authentication is per endpoint: every
    // route and every funnel carries its own <see cref="ProxyKey"/>, so a key leaked from one
    // client cannot spend the OAuth grant attached to a different route, and revoking one client
    // is a single-endpoint operation rather than a re-key of everything.
}
