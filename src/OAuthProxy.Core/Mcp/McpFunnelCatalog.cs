using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;

namespace OAuthProxy.Core.Mcp;

/// <summary>What one source currently offers, as last discovered.</summary>
public sealed record McpSourceCatalog(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Prompts,
    DateTimeOffset RetrievedUtc,
    string? Error)
{
    public static McpSourceCatalog Failed(string error) =>
        new([], [], [], DateTimeOffset.UtcNow, error);

    public bool IsEmpty => Tools.Count == 0 && Resources.Count == 0 && Prompts.Count == 0;

    /// <summary>One-line summary for the sources grid.</summary>
    public string Describe()
    {
        if (Error is not null) return $"⚠ {Error}";

        // Says "connected" explicitly, because the alternative reading of an empty result —
        // that the source could not be reached — is the one a user will assume otherwise.
        if (IsEmpty) return "connected — server offers nothing";

        var parts = new List<string>();
        if (Tools.Count > 0) parts.Add($"{Tools.Count} tools");
        if (Resources.Count > 0) parts.Add($"{Resources.Count} resources");
        if (Prompts.Count > 0) parts.Add($"{Prompts.Count} prompts");

        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Last-known catalog per source, populated by the GUI's Refresh. Purely a UI convenience — the
/// funnel handlers always ask the upstream directly, so a stale entry here can never cause a
/// stale tool list to be served to an agent.
/// </summary>
public sealed class McpCatalogCache
{
    private readonly ConcurrentDictionary<Guid, McpSourceCatalog> _catalogs = new();

    public McpSourceCatalog? Get(Guid sourceId) =>
        _catalogs.TryGetValue(sourceId, out var catalog) ? catalog : null;

    public void Set(Guid sourceId, McpSourceCatalog catalog) => _catalogs[sourceId] = catalog;

    public void Remove(Guid sourceId) => _catalogs.TryRemove(sourceId, out _);
}

/// <summary>
/// Pagination drain plus the field-by-field clones the funnel needs when it renames a primitive.
///
/// The protocol objects are mutable, but the ones coming back from a source belong to that
/// source's client and may be handed out again — mutating a Tool's Name in place would rename it
/// for every funnel sharing the catalog. Every rename therefore produces a copy.
/// </summary>
internal static class McpProtocolExtensions
{
    /// <summary>
    /// Upper bound on pages pulled from one source. A funnel answers tools/list as a single
    /// unpaginated page (there is no sane way to interleave several upstreams' opaque cursors
    /// into one), so a source that never stops paging would otherwise hang the request forever.
    /// </summary>
    public const int MaxPages = 50;

    public static Tool WithName(this Tool tool, string name) => new()
    {
        Name = name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema,
        Annotations = tool.Annotations,
        Icons = tool.Icons,
        Meta = tool.Meta,
    };

    public static Prompt WithName(this Prompt prompt, string name) => new()
    {
        Name = name,
        Title = prompt.Title,
        Description = prompt.Description,
        Arguments = prompt.Arguments,
        Icons = prompt.Icons,
        Meta = prompt.Meta,
    };

    public static Resource WithNameAndUri(this Resource resource, string name, string uri) => new()
    {
        Name = name,
        Uri = uri,
        Title = resource.Title,
        Description = resource.Description,
        MimeType = resource.MimeType,
        Size = resource.Size,
        Annotations = resource.Annotations,
        Icons = resource.Icons,
        Meta = resource.Meta,
    };

    public static ResourceTemplate WithNameAndTemplate(this ResourceTemplate template, string name, string uriTemplate) => new()
    {
        Name = name,
        UriTemplate = uriTemplate,
        Title = template.Title,
        Description = template.Description,
        MimeType = template.MimeType,
        Annotations = template.Annotations,
        Icons = template.Icons,
        Meta = template.Meta,
    };
}
