using System.Collections.Concurrent;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Mcp;

/// <summary>
/// Holds the MCP client sessions a funnel talks to its sources through.
///
/// Keyed by (funnel, source) rather than by source alone, and that is the whole point. An MCP
/// session is stateful — the upstream may hang pagination cursors, resource subscriptions, or
/// its own notion of "current context" off it. Sharing one session between two funnels would let
/// one agent's activity perturb another's, and would collapse both endpoints together the moment
/// that single session expired. Per-edge keying makes every local endpoint behave like a
/// standalone MCP client of that upstream: independent state, independent failure.
///
/// Requests are *not* serialized per session. McpClient multiplexes concurrent requests over one
/// connection by JSON-RPC id, so several calls on the same edge stay in flight together and
/// responses find their own caller. The pool's only job is handing out the right session.
/// </summary>
public sealed class McpSourceConnectionPool : IAsyncDisposable
{
    /// <summary>
    /// Funnel id used by the GUI's "Refresh" discovery. Discovery must not borrow a live funnel's
    /// session: a manual refresh in the UI would otherwise be able to disturb — or, on failure,
    /// tear down — a session an agent is in the middle of using.
    /// </summary>
    public static readonly Guid DiscoveryFunnelId = Guid.Empty;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly ConfigStoreCache _configStoreCache;
    private readonly ActivityLog _activityLog;

    private readonly ConcurrentDictionary<ConnectionKey, ConnectionEntry> _connections = new();
    private volatile bool _disposed;

    public McpSourceConnectionPool(ConfigStoreCache configStoreCache, ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _activityLog = activityLog;
    }

    private readonly record struct ConnectionKey(Guid FunnelId, Guid SourceId);

    private sealed class ConnectionEntry
    {
        public required Task<McpClient> Client { get; init; }
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Runs one operation against a source's session for a funnel.
    ///
    /// <paramref name="isIdempotent"/> decides what happens when the session turns out to be
    /// dead — an expired Mcp-Session-Id (the upstream answers 404), a restarted server, or an
    /// idle SSE stream that YARP's 30-minute activity timeout cut. For a list or a read the
    /// session is rebuilt and the operation runs once more, which is invisible and correct. For
    /// tools/call it is not: the upstream may have executed the call before the transport
    /// failed, and silently repeating a side effect is worse than surfacing the error. There the
    /// session is dropped so the *next* call reconnects, and this one fails.
    /// </summary>
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        Guid funnelId,
        McpSourceRecord source,
        Func<McpClient, CancellationToken, ValueTask<TResult>> operation,
        bool isIdempotent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = new ConnectionKey(funnelId, source.Id);

        try
        {
            var client = await GetOrCreateAsync(key, source, cancellationToken).ConfigureAwait(false);
            return await operation(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away; the session is fine and must survive.
            throw;
        }
        catch (McpException)
        {
            // The server replied — with a refusal, but it replied. "Method not found:
            // resources/list" from a tools-only server is the common case, and treating it as a
            // dead connection tore down a working session and re-handshook on every discovery
            // pass. A protocol error says nothing about the transport, so the session stands.
            throw;
        }
        catch (Exception ex)
        {
            await InvalidateAsync(key).ConfigureAwait(false);

            if (!isIdempotent)
            {
                _activityLog.Log($"MCP source '{source.Name}' failed and its session was dropped — {ex.Message}");
                throw;
            }

            _activityLog.Log($"MCP source '{source.Name}' session was stale, reconnecting — {ex.Message}");

            var client = await GetOrCreateAsync(key, source, cancellationToken).ConfigureAwait(false);
            return await operation(client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<McpClient> GetOrCreateAsync(ConnectionKey key, McpSourceRecord source, CancellationToken cancellationToken)
    {
        EvictIdle();

        // The task — not the completed client — is what gets cached, so concurrent first callers
        // await one handshake instead of racing to open several sessions to the same upstream.
        var entry = _connections.GetOrAdd(key, _ => new ConnectionEntry
        {
            Client = ConnectAsync(source, CancellationToken.None),
        });

        entry.LastUsedUtc = DateTimeOffset.UtcNow;

        try
        {
            return await entry.Client.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed handshake must not be cached, or the source stays broken until restart
            // even after whatever caused it is fixed.
            await InvalidateAsync(key).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<McpClient> ConnectAsync(McpSourceRecord source, CancellationToken cancellationToken)
    {
        var options = BuildTransportOptions(source);
        var transport = new HttpClientTransport(options);

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        _activityLog.Log($"MCP source '{source.Name}' connected ({client.ServerInfo?.Name ?? "unnamed server"})");

        return client;
    }

    /// <summary>
    /// Builds the transport for a source. A route-backed source is dialled back through this
    /// app's own listener so the request takes the ordinary proxied path — LocalAccessGuard, then
    /// YARP, then the credential transform — and picks up the OAuth token with zero duplication
    /// of that logic here.
    /// </summary>
    public HttpClientTransportOptions BuildTransportOptions(McpSourceRecord source)
    {
        var store = _configStoreCache.Current;
        var headers = new Dictionary<string, string>();
        Uri endpoint;

        if (source.Kind == McpSourceKind.ProxyRoute)
        {
            var route = store.Routes.FirstOrDefault(r => r.Id == source.RouteId)
                        ?? throw new InvalidOperationException(
                            $"MCP source '{source.Name}' points at a route that no longer exists.");

            var prefix = route.PathPrefix.TrimEnd('/');
            endpoint = new Uri($"http://127.0.0.1:{store.Settings.ListenPort}{prefix}");

            // Read live rather than captured at construction, so regenerating the key in the
            // Settings tab doesn't leave every funnel authenticating with a stale one.
            headers[LocalAccessGuard.ApiKeyHeaderName] = store.Settings.LocalApiKey;

            // Marks the request as originating from the funnel itself. The funnel gate refuses
            // anything already carrying it, which is what stops a source that resolves back to
            // /mcp from recursing.
            headers[LocalAccessGuard.FunnelHopHeaderName] = "1";
        }
        else
        {
            endpoint = new Uri(source.Url);
        }

        return new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = source.Name,
            TransportMode = source.Transport switch
            {
                McpTransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
                McpTransportPreference.Sse => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect,
            },
            // Generous on purpose. A cold-starting serverless MCP server — a Google Apps Script
            // deployment is the case that forced this — can take the better part of a minute to
            // answer its first initialize, and a timeout there is indistinguishable from "this
            // server has no tools". Nothing is held open while waiting, so the only cost of a
            // long ceiling is how long a genuinely dead source takes to report itself.
            ConnectionTimeout = TimeSpan.FromMinutes(2),
            AdditionalHeaders = headers,
        };
    }

    /// <summary>
    /// Asks a source what it currently offers, for the GUI's selection lists.
    ///
    /// Runs on the reserved discovery key rather than any funnel's, so a manual Refresh cannot
    /// borrow — or, if it fails, tear down — a session an agent is in the middle of using.
    /// Errors are returned rather than thrown: an unreachable source should colour one row in the
    /// grid, not raise a dialog.
    /// </summary>
    public async Task<McpSourceCatalog> DiscoverAsync(McpSourceRecord source, CancellationToken cancellationToken = default)
    {
        try
        {
            // Connect first, as its own step. Previously the handshake happened inside the
            // per-capability listing below, whose catch swallowed everything — so a source that
            // could not be reached at all came back as an empty catalog with no error, and the
            // UI reported "connected — nothing offered". A dead upstream and a server with no
            // tools looked identical, which is the worst possible answer to "why are there no
            // tools?".
            await ExecuteAsync(
                DiscoveryFunnelId,
                source,
                static (client, _) => ValueTask.FromResult(client.ServerInfo?.Name ?? ""),
                isIdempotent: true,
                cancellationToken).ConfigureAwait(false);

            var tools = await ListAsync(source, (client, ct) => client.ListToolsAsync(cancellationToken: ct), t => t.Name, cancellationToken);
            var resources = await ListAsync(source, (client, ct) => client.ListResourcesAsync(cancellationToken: ct), r => r.Uri, cancellationToken);
            var prompts = await ListAsync(source, (client, ct) => client.ListPromptsAsync(cancellationToken: ct), p => p.Name, cancellationToken);

            return new McpSourceCatalog(tools, resources, prompts, DateTimeOffset.UtcNow, Error: null);
        }
        catch (Exception ex)
        {
            _activityLog.Log($"MCP source '{source.Name}' could not be reached — {ex.Message}");
            return McpSourceCatalog.Failed(Describe(ex));
        }
    }

    /// <summary>
    /// One primitive kind for <see cref="DiscoverAsync"/>.
    ///
    /// Only a protocol-level refusal is tolerated: a server that implements no prompts answers
    /// "method not found", and most servers offer tools and nothing else, so treating that as a
    /// fault would mark almost every healthy source as broken. A transport failure is a
    /// different thing entirely and is left to propagate — the session was established a moment
    /// ago, so it means something genuinely went wrong.
    /// </summary>
    private async Task<List<string>> ListAsync<TItem>(
        McpSourceRecord source,
        Func<McpClient, CancellationToken, ValueTask<IList<TItem>>> list,
        Func<TItem, string> name,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await ExecuteAsync(DiscoveryFunnelId, source, list, isIdempotent: true, cancellationToken)
                .ConfigureAwait(false);

            return [.. items.Select(name)];
        }
        catch (McpException)
        {
            return [];
        }
    }

    /// <summary>
    /// A message worth putting in a grid cell. Transport failures nest the useful part one or
    /// two levels down — the outer text is usually "An error occurred while sending the request".
    /// </summary>
    private static string Describe(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }

        return ReferenceEquals(innermost, ex) ? ex.Message : $"{ex.Message} ({innermost.Message})";
    }

    /// <summary>Drops every session belonging to one funnel — used when that funnel is edited or deleted.</summary>
    public Task InvalidateFunnelAsync(Guid funnelId) =>
        InvalidateWhereAsync(key => key.FunnelId == funnelId);

    /// <summary>Drops every session to one source, across all funnels — used when the source is edited or deleted.</summary>
    public Task InvalidateSourceAsync(Guid sourceId) =>
        InvalidateWhereAsync(key => key.SourceId == sourceId);

    public Task InvalidateAllAsync() => InvalidateWhereAsync(_ => true);

    private async Task InvalidateWhereAsync(Func<ConnectionKey, bool> predicate)
    {
        foreach (var key in _connections.Keys.Where(predicate).ToList())
        {
            await InvalidateAsync(key).ConfigureAwait(false);
        }
    }

    private static async Task InvalidateAsync(ConnectionEntry entry)
    {
        try
        {
            var client = await entry.Client.ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Already broken, or never connected. Either way there is nothing left to close.
        }
    }

    private async Task InvalidateAsync(ConnectionKey key)
    {
        if (_connections.TryRemove(key, out var entry))
        {
            await InvalidateAsync(entry).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opportunistic, on the access path rather than on a timer — the dictionary holds one entry
    /// per funnel-source edge, so it is tiny and a scan costs nothing worth a background service.
    /// </summary>
    private void EvictIdle()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;

        foreach (var (key, entry) in _connections)
        {
            if (entry.LastUsedUtc >= cutoff) continue;
            if (!_connections.TryRemove(key, out var removed)) continue;

            _ = InvalidateAsync(removed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await InvalidateAllAsync().ConfigureAwait(false);
    }
}
