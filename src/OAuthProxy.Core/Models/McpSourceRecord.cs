using System.Text.Json.Serialization;

namespace OAuthProxy.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpSourceKind
{
    /// <summary>
    /// An MCP server reached through one of this proxy's own routes, so the route's OAuth
    /// credential is attached on the way out. Dialled back through the loopback listener rather
    /// than short-circuited in process — that reuses LocalAccessGuard, YARP, and the credential
    /// transform exactly as an external client would experience them, with no second code path
    /// to keep in sync.
    /// </summary>
    ProxyRoute,

    /// <summary>An MCP server that needs no credential, addressed directly by URL.</summary>
    RemoteUrl,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransportPreference
{
    /// <summary>Let the SDK probe for streamable HTTP and fall back to legacy SSE.</summary>
    Auto,
    StreamableHttp,
    Sse,
}

/// <summary>
/// One connectable MCP server. Reusable across funnels: the same source can appear in several
/// funnels with different tool selections, and each of those gets its own upstream session (see
/// McpSourceConnectionPool).
/// </summary>
public sealed class McpSourceRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name only. <see cref="Alias"/> is what appears in tool names.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Prefix stamped onto every name this source contributes, so two upstreams offering a
    /// "search" tool stay distinguishable and a call can be routed back to the right one.
    /// </summary>
    public required string Alias { get; set; }

    public McpSourceKind Kind { get; set; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="McpSourceKind.ProxyRoute"/>.</summary>
    public Guid RouteId { get; set; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="McpSourceKind.RemoteUrl"/>.</summary>
    public string Url { get; set; } = "";

    public McpTransportPreference Transport { get; set; } = McpTransportPreference.Auto;

    public bool Enabled { get; set; } = true;
}
