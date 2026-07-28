using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Proxy;

/// <summary>
/// The only thing standing between a local caller and the user's OAuth grant.
///
/// Binding Kestrel to 127.0.0.1 keeps off-host traffic out but is *not* an authorization
/// boundary: every process on the machine, under any user account, can reach loopback. Since
/// the proxy attaches a live access token to whatever it forwards, an unguarded listener is a
/// confused deputy that lends the user's Google/Nextcloud session to the first caller who asks.
///
/// Three checks, each closing a different door:
///   1. Shared secret  — a caller must know a value it cannot guess from the port alone.
///   2. Host allowlist — blocks DNS rebinding, where a page on evil.com re-resolves that name
///                       to 127.0.0.1 so the browser treats proxied responses as same-origin
///                       and lets attacker JavaScript read the user's data.
///   3. No Origin      — a browser only sends Origin on cross-site requests; a legitimate
///                       local client (MCP host, curl, a script) never does.
/// </summary>
public static class LocalAccessGuard
{
    public const string ApiKeyHeaderName = "X-Proxy-Key";

    /// <summary>
    /// Fallback for clients that physically cannot set headers — browser EventSource, used by
    /// some MCP SSE transports, is the motivating case. Logging redacts it (see ActivityLog
    /// callers) so it never lands on disk.
    /// </summary>
    public const string ApiKeyQueryName = "proxy_key";

    private static readonly string[] AllowedHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

    public static IApplicationBuilder UseLocalAccessGuard(this IApplicationBuilder app)
    {
        var configStoreCache = app.ApplicationServices.GetService(typeof(ConfigStoreCache)) as ConfigStoreCache
                               ?? throw new InvalidOperationException("ConfigStoreCache is not registered.");
        var activityLog = app.ApplicationServices.GetService(typeof(ActivityLog)) as ActivityLog
                          ?? throw new InvalidOperationException("ActivityLog is not registered.");

        return app.Use(async (context, next) =>
        {
            var rejection = Reject(context, configStoreCache);

            // Unconditionally, whether or not the request was allowed: the key authenticates
            // the caller to *this* proxy and has no business reaching the upstream, which will
            // happily write it to its own access log. YARP forwards both headers and the query
            // string verbatim, so it has to come off here, before the request goes anywhere.
            StripApiKeyFromRequest(context.Request);

            if (rejection is { } reason)
            {
                activityLog.Log($"DENIED {context.Request.Method} {context.Request.Path} — {reason}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "Forbidden. This proxy requires the local API key from OAuthProxy's Settings tab, "
                    + $"sent as the '{ApiKeyHeaderName}' header.");
                return;
            }

            // Upstreams that answer with permissive CORS (Access-Control-Allow-Origin: *) would
            // otherwise hand any web page the ability to read proxied responses directly, since
            // YARP copies response headers through verbatim. Strip them on the way out.
            context.Response.OnStarting(() =>
            {
                foreach (var header in context.Response.Headers.Keys
                             .Where(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    context.Response.Headers.Remove(header);
                }
                return Task.CompletedTask;
            });

            await next();
        });
    }

    /// <summary>
    /// Removes the local API key from the request in both forms it can arrive in, so it is
    /// never forwarded upstream and never reaches the activity log. Everything else is
    /// preserved untouched — callers legitimately pass their own headers and parameters (an
    /// upstream's own <c>?token=</c>, for instance), and those still have to arrive intact.
    /// </summary>
    private static void StripApiKeyFromRequest(HttpRequest request)
    {
        request.Headers.Remove(ApiKeyHeaderName);

        if (!request.Query.ContainsKey(ApiKeyQueryName)) return;

        var remaining = request.Query
            .Where(parameter => !string.Equals(parameter.Key, ApiKeyQueryName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Value.Select(value =>
                new KeyValuePair<string, string?>(parameter.Key, value)));

        // Assigning QueryString also invalidates the cached parsed Query collection, so later
        // readers (the transform's logging, YARP's forwarder) see the trimmed version.
        request.QueryString = QueryString.Create(remaining);
    }

    /// <summary>Returns null when the request is allowed, or a short reason when it is not.</summary>
    private static string? Reject(HttpContext context, ConfigStoreCache configStoreCache)
    {
        var request = context.Request;

        // Host carries the port; compare only the name part so any listen port works.
        var host = request.Host.Host;
        if (!AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return $"host '{host}' is not loopback (possible DNS-rebinding attempt)";
        }

        if (request.Headers.ContainsKey("Origin"))
        {
            return "request carries an Origin header, so it came from a web page";
        }

        // Defense in depth, not a live hole. Kestrel percent-decodes the target and *then*
        // removes dot segments, so "%2e%2e%2f" is already resolved by the time routing runs —
        // verified end to end in ProxyForwardingTests over a raw socket, since System.Uri
        // normalizes this away client-side before HttpClient can even send it.
        //
        // Kept because the confinement of a caller to one upstream area rests entirely on that
        // normalization order, and nothing else in this pipeline would notice if it changed:
        // a surviving "../" would let a caller climb above an upstream's base path with the
        // user's access token attached. Costs one string scan per request.
        if (HasDotSegment(request.Path))
        {
            return "path contains a '..' segment";
        }

        var expected = configStoreCache.Current.Settings.LocalApiKey;
        if (string.IsNullOrEmpty(expected))
        {
            // A store written before this key existed deserializes it as null. Failing closed
            // would brick an upgraded install with no way in, so treat it as "not yet
            // configured" and let ConfigStoreCache backfill one on load instead.
            return "no local API key is configured";
        }

        var presented = request.Headers[ApiKeyHeaderName].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            presented = request.Query[ApiKeyQueryName].ToString();
        }

        return FixedTimeEquals(presented, expected) ? null : "missing or incorrect local API key";
    }

    /// <summary>
    /// Whole-segment match only. A file legitimately named "notes..txt" is not traversal, and
    /// rejecting every path that merely contains two dots would break real upstream URLs.
    /// </summary>
    private static bool HasDotSegment(PathString path)
    {
        if (path.Value is not { } value || !value.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var segment in value.Split('/'))
        {
            if (segment == "..") return true;
        }

        return false;
    }

    /// <summary>Length-independent, content-constant-time comparison — no early exit to time against.</summary>
    private static bool FixedTimeEquals(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // CryptographicOperations.FixedTimeEquals returns early on a length mismatch, which
        // leaks the expected length. Hashing both sides first makes every comparison run over
        // the same 32 bytes regardless of input size.
        Span<byte> presentedHash = stackalloc byte[32];
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(presentedBytes, presentedHash);
        SHA256.HashData(expectedBytes, expectedHash);

        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
