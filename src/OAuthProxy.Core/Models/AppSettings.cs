using System.Security.Cryptography;

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

    /// <summary>
    /// Shared secret every proxied request must present. Binding to loopback is not an
    /// authorization boundary — without this, any process on the machine (or any web page,
    /// via DNS rebinding) can spend the user's OAuth grant just by knowing the port.
    /// Generated on first run and persisted in the DPAPI-encrypted store.
    ///
    /// Deliberately defaults to empty rather than to a generated key: a property initializer
    /// would hand every load a fresh key, which then looks "already set" to the backfill in
    /// ConfigStoreCache.InitializeAsync and never reaches disk — so each restart would invent
    /// a different key and silently break every configured client. Generation happens in one
    /// place, immediately followed by a save.
    /// </summary>
    public string LocalApiKey { get; set; } = "";

    public static string GenerateApiKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
