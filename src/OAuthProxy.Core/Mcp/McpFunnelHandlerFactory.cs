using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Mcp;

/// <summary>
/// Turns one funnel into a set of MCP server handlers that fan out to its sources.
///
/// Everything is resolved from the config store at request time, never captured when the endpoint
/// was mapped. That is what makes an edit in the GUI — unticking a tool, adding a source —
/// visible on the agent's very next call, with no session to invalidate and no
/// notifications/tools/list_changed to plumb.
///
/// The two rules that matter:
///   • A source that is down degrades only itself. Listing catches per source and returns what
///     the healthy ones offered, because one unreachable server must not blank an agent's whole
///     toolset.
///   • Filtering is enforced on the call path as well as the list path. An agent that learned a
///     tool name before it was unticked would otherwise keep calling it successfully.
/// </summary>
public sealed class McpFunnelHandlerFactory
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly McpSourceConnectionPool _connectionPool;
    private readonly ActivityLog _activityLog;

    public McpFunnelHandlerFactory(
        ConfigStoreCache configStoreCache,
        McpSourceConnectionPool connectionPool,
        ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _connectionPool = connectionPool;
        _activityLog = activityLog;
    }

    /// <summary>A funnel's membership row paired with the source record it points at.</summary>
    private readonly record struct SourceLink(McpFunnelSource Link, McpSourceRecord Source);

    public McpFunnelRecord? FindFunnel(string? slug) =>
        string.IsNullOrEmpty(slug)
            ? null
            : _configStoreCache.Current.McpFunnels
                .FirstOrDefault(f => f.Enabled && string.Equals(f.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public McpServerHandlers Create(Guid funnelId, string funnelName) => new()
    {
        ListToolsHandler = (_, ct) => ListToolsAsync(funnelId, funnelName, ct),
        CallToolHandler = (request, ct) => CallToolAsync(funnelId, funnelName, request, ct),
        ListPromptsHandler = (_, ct) => ListPromptsAsync(funnelId, funnelName, ct),
        GetPromptHandler = (request, ct) => GetPromptAsync(funnelId, request, ct),
        ListResourcesHandler = (_, ct) => ListResourcesAsync(funnelId, funnelName, ct),
        ListResourceTemplatesHandler = (_, ct) => ListResourceTemplatesAsync(funnelId, ct),
        ReadResourceHandler = (request, ct) => ReadResourceAsync(funnelId, request, ct),
    };

    private List<SourceLink> ResolveSources(Guid funnelId)
    {
        var store = _configStoreCache.Current;
        var funnel = store.McpFunnels.FirstOrDefault(f => f.Id == funnelId);
        if (funnel is null) return [];

        var resolved = new List<SourceLink>();

        foreach (var link in funnel.Sources)
        {
            var source = store.McpSources.FirstOrDefault(s => s.Id == link.SourceId);
            if (source is null || !source.Enabled) continue;

            resolved.Add(new SourceLink(link, source));
        }

        return resolved;
    }

    private SourceLink? ResolveByAlias(Guid funnelId, string alias) =>
        ResolveSources(funnelId)
            .Cast<SourceLink?>()
            .FirstOrDefault(s => string.Equals(s!.Value.Source.Alias, alias, StringComparison.OrdinalIgnoreCase));

    // ---- tools -----------------------------------------------------------------------------

    private async ValueTask<ListToolsResult> ListToolsAsync(Guid funnelId, string funnelName, CancellationToken ct)
    {
        var result = new ListToolsResult();
        var failures = new List<string>();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var tool in await DrainAsync(
                             funnelId, source, ct,
                             (client, cursor, token) => client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, token),
                             page => (page.Tools, page.NextCursor)))
                {
                    if (!link.AllowsTool(tool.Name)) continue;

                    if (McpNameMapper.IsTruncated(source.Alias, tool.Name))
                    {
                        // Exposing it would produce a name that cannot be routed back.
                        _activityLog.Log($"MCP funnel '{funnelName}' skipped '{source.Alias}' tool '{tool.Name}' — prefixed name exceeds {McpNameMapper.MaxNameLength} characters");
                        continue;
                    }

                    result.Tools.Add(tool.WithName(McpNameMapper.Encode(source.Alias, tool.Name)));
                }
            }
            catch (Exception ex)
            {
                failures.Add(source.Alias);
                _activityLog.Log($"MCP funnel '{funnelName}' could not list tools from '{source.Name}' — {ex.Message}");
            }
        }

        _activityLog.Log($"MCP funnel '{funnelName}' tools/list -> {result.Tools.Count} tools{DescribeFailures(failures)}");

        return result;
    }

    private async ValueTask<CallToolResult> CallToolAsync(
        Guid funnelId, string funnelName, RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var exposedName = request.Params?.Name ?? "";

        if (!McpNameMapper.TryDecode(exposedName, out var alias, out var upstreamName))
        {
            throw new McpException($"Unknown tool '{exposedName}'.");
        }

        if (ResolveByAlias(funnelId, alias) is not { } match)
        {
            throw new McpException($"Unknown tool '{exposedName}'.");
        }

        // Deliberately the same message as "unknown": whether a tool exists upstream but was
        // filtered out is not something a caller of this funnel is entitled to learn.
        if (!match.Link.AllowsTool(upstreamName))
        {
            throw new McpException($"Unknown tool '{exposedName}'.");
        }

        try
        {
            var result = await _connectionPool.ExecuteAsync(
                funnelId,
                match.Source,
                (client, token) => client.CallToolAsync(
                    new CallToolRequestParams { Name = upstreamName, Arguments = request.Params?.Arguments },
                    token),
                isIdempotent: false,
                ct).ConfigureAwait(false);

            // Arguments are never logged — they routinely carry the user's own data.
            _activityLog.Log($"MCP funnel '{funnelName}' call {exposedName} -> {(result.IsError == true ? "tool error" : "ok")}");

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activityLog.Log($"MCP funnel '{funnelName}' call {exposedName} -> failed: {ex.Message}");
            throw;
        }
    }

    // ---- prompts ---------------------------------------------------------------------------

    private async ValueTask<ListPromptsResult> ListPromptsAsync(Guid funnelId, string funnelName, CancellationToken ct)
    {
        var result = new ListPromptsResult();
        var failures = new List<string>();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var prompt in await DrainAsync(
                             funnelId, source, ct,
                             (client, cursor, token) => client.ListPromptsAsync(new ListPromptsRequestParams { Cursor = cursor }, token),
                             page => (page.Prompts, page.NextCursor)))
                {
                    if (!link.AllowsPrompt(prompt.Name)) continue;
                    if (McpNameMapper.IsTruncated(source.Alias, prompt.Name)) continue;

                    result.Prompts.Add(prompt.WithName(McpNameMapper.Encode(source.Alias, prompt.Name)));
                }
            }
            catch (Exception ex)
            {
                failures.Add(source.Alias);
                _activityLog.Log($"MCP funnel '{funnelName}' could not list prompts from '{source.Name}' — {ex.Message}");
            }
        }

        return result;
    }

    private async ValueTask<GetPromptResult> GetPromptAsync(
        Guid funnelId, RequestContext<GetPromptRequestParams> request, CancellationToken ct)
    {
        var exposedName = request.Params?.Name ?? "";

        if (!McpNameMapper.TryDecode(exposedName, out var alias, out var upstreamName) ||
            ResolveByAlias(funnelId, alias) is not { } match ||
            !match.Link.AllowsPrompt(upstreamName))
        {
            throw new McpException($"Unknown prompt '{exposedName}'.");
        }

        return await _connectionPool.ExecuteAsync(
            funnelId,
            match.Source,
            (client, token) => client.GetPromptAsync(
                new GetPromptRequestParams { Name = upstreamName, Arguments = request.Params?.Arguments },
                token),
            isIdempotent: true,
            ct).ConfigureAwait(false);
    }

    // ---- resources -------------------------------------------------------------------------

    private async ValueTask<ListResourcesResult> ListResourcesAsync(Guid funnelId, string funnelName, CancellationToken ct)
    {
        var result = new ListResourcesResult();
        var failures = new List<string>();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var resource in await DrainAsync(
                             funnelId, source, ct,
                             (client, cursor, token) => client.ListResourcesAsync(new ListResourcesRequestParams { Cursor = cursor }, token),
                             page => (page.Resources, page.NextCursor)))
                {
                    if (!link.AllowsResource(resource.Uri)) continue;

                    result.Resources.Add(resource.WithNameAndUri(
                        McpNameMapper.Encode(source.Alias, resource.Name),
                        McpNameMapper.EncodeResourceUri(source.Alias, resource.Uri)));
                }
            }
            catch (Exception ex)
            {
                failures.Add(source.Alias);
                _activityLog.Log($"MCP funnel '{funnelName}' could not list resources from '{source.Name}' — {ex.Message}");
            }
        }

        return result;
    }

    private async ValueTask<ListResourceTemplatesResult> ListResourceTemplatesAsync(Guid funnelId, CancellationToken ct)
    {
        var result = new ListResourceTemplatesResult();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var template in await DrainAsync(
                             funnelId, source, ct,
                             (client, cursor, token) => client.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams { Cursor = cursor }, token),
                             page => (page.ResourceTemplates, page.NextCursor)))
                {
                    if (!link.AllowsResource(template.UriTemplate)) continue;

                    result.ResourceTemplates.Add(template.WithNameAndTemplate(
                        McpNameMapper.Encode(source.Alias, template.Name),
                        McpNameMapper.EncodeResourceUriTemplate(source.Alias, template.UriTemplate)));
                }
            }
            catch
            {
                // Templates are optional and many servers have none; a failure here is not worth
                // a log line of its own, since listing resources against the same source already
                // reported it.
            }
        }

        return result;
    }

    private async ValueTask<ReadResourceResult> ReadResourceAsync(
        Guid funnelId, RequestContext<ReadResourceRequestParams> request, CancellationToken ct)
    {
        var exposedUri = request.Params?.Uri ?? "";

        if (!McpNameMapper.TryDecodeResourceUri(exposedUri, out var alias, out var upstreamUri) ||
            ResolveByAlias(funnelId, alias) is not { } match)
        {
            throw new McpException($"Unknown resource '{exposedUri}'.");
        }

        // A template-derived URI is not in the selection list by its expanded form, so a strict
        // membership test would break every templated read. Only an explicit Exclude entry —
        // which names a URI the user deliberately blocked — is enforced here.
        if (match.Link.ResourceMode == McpSelectionMode.Exclude && !match.Link.AllowsResource(upstreamUri))
        {
            throw new McpException($"Unknown resource '{exposedUri}'.");
        }

        return await _connectionPool.ExecuteAsync(
            funnelId,
            match.Source,
            (client, token) => client.ReadResourceAsync(new ReadResourceRequestParams { Uri = upstreamUri }, token),
            isIdempotent: true,
            ct).ConfigureAwait(false);
    }

    // ---- plumbing --------------------------------------------------------------------------

    /// <summary>
    /// Pulls every page a source will give for one primitive kind and returns them as one list.
    ///
    /// The funnel cannot forward cursors: they are opaque strings minted by whichever upstream
    /// issued them, and several sources' cursors cannot be combined into one the client could
    /// send back. Draining here is what lets the funnel answer as a single unpaginated page.
    /// </summary>
    private async ValueTask<List<TItem>> DrainAsync<TPage, TItem>(
        Guid funnelId,
        McpSourceRecord source,
        CancellationToken ct,
        Func<McpClient, string?, CancellationToken, ValueTask<TPage>> fetchPage,
        Func<TPage, (IList<TItem> Items, string? NextCursor)> readPage)
    {
        var items = new List<TItem>();
        string? cursor = null;

        for (var page = 0; page < McpProtocolExtensions.MaxPages; page++)
        {
            var captured = cursor;

            var response = await _connectionPool.ExecuteAsync(
                funnelId,
                source,
                (client, token) => fetchPage(client, captured, token),
                isIdempotent: true,
                ct).ConfigureAwait(false);

            var (pageItems, nextCursor) = readPage(response);
            items.AddRange(pageItems);

            if (string.IsNullOrEmpty(nextCursor)) break;
            cursor = nextCursor;
        }

        return items;
    }

    private static string DescribeFailures(List<string> failures) =>
        failures.Count == 0 ? "" : $" ({failures.Count} source(s) unavailable: {string.Join(", ", failures)})";
}
