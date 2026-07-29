using OAuthProxy.Core.Auth;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace OAuthProxy.Core.Proxy;

/// <summary>
/// Attaches the route's credential to each proxied request, in whichever shape the route is
/// configured for — a header (the "Authorization: Bearer &lt;token&gt;" default), a query
/// parameter, or a field in the request body.
///
/// The token is read from ConfigStoreCache live on every request (not captured at route-build
/// time). This is what decouples token refresh from route rebuilds — a refreshed token applies
/// to the very next proxied request automatically, no config reload needed.
/// </summary>
public sealed class CredentialInjectionTransformProvider(
    ConfigStoreCache configStoreCache,
    AccessTokenProvider accessTokenProvider,
    ActivityLog activityLog) : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context)
    {
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
    }

    public void Apply(TransformBuilderContext context)
    {
        if (context.Route.Metadata is not { } metadata ||
            !metadata.TryGetValue(ProxyConfigBuilder.CredentialIdMetadataKey, out var credentialIdText) ||
            !Guid.TryParse(credentialIdText, out var credentialId))
        {
            return;
        }

        var injection = ProxyConfigBuilder.ReadCredentialInjection(metadata);

        context.AddRequestTransform(async transformContext =>
        {
            var credential = configStoreCache.GetCredential(credentialId);

            // Not credential.Token.AccessToken directly: this refreshes first if the token has
            // already expired, so a request arriving between refresh-loop ticks (or after the
            // machine slept through one) still goes out authenticated instead of 401-ing.
            var token = await accessTokenProvider.GetAccessTokenAsync(
                credentialId, transformContext.HttpContext.RequestAborted);

            // Cleared unconditionally — whatever the route's placement is, and whether or not we
            // have a token to attach. YARP copies request headers through by default, so leaving
            // these alone would forward the caller's own Authorization header and cookies to the
            // upstream, letting a local caller use this proxy to spend credentials (or an
            // ambient browser session) it should not have been able to reach.
            transformContext.ProxyRequest.Headers.Authorization = null;
            transformContext.ProxyRequest.Headers.Remove("Cookie");

            var attached = token is not null
                           && await InjectAsync(transformContext, injection, token, credential?.Name);

            var request = transformContext.HttpContext.Request;
            var placement = $"{injection.Placement.ToString().ToLowerInvariant()} {injection.Name}";
            activityLog.Log(attached
                ? $"PROXY {request.Method} {LogSafePath(request)} -> {transformContext.DestinationPrefix} [token: {credential?.Name} via {placement}]"
                : $"PROXY {request.Method} {LogSafePath(request)} -> {transformContext.DestinationPrefix} "
                  + $"[NO TOKEN - {(token is null ? "credential not connected" : $"could not be attached to {placement}")}]");
        });

        context.AddResponseTransform(transformContext =>
        {
            var status = transformContext.ProxyResponse?.StatusCode;
            var request = transformContext.HttpContext.Request;

            // The request body has already been streamed upstream by this point, so replaying
            // it here is not possible. Flagging the credential is the next best thing: the
            // periodic loop picks it up and the user sees "Needs reconnect" in the UI, instead
            // of silent 401s with no indication of which credential went bad.
            if (status == System.Net.HttpStatusCode.Unauthorized)
            {
                var credential = configStoreCache.GetCredential(credentialId);
                if (credential is not null)
                {
                    activityLog.Log(
                        $"AUTH '{credential.Name}' rejected by upstream (401) — token refresh will be retried, "
                        + "reconnect if this repeats");
                    credential.NeedsReconnect = credential.Token?.RefreshToken is null;
                }
            }

            activityLog.Log(
                $"  <- {(status is null ? "no response (upstream unreachable)" : ((int)status).ToString())}"
                + $"{DescribeContentType(transformContext.ProxyResponse)} for {request.Method} {LogSafePath(request)}");
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// The response's media type, for any status that is not a plain success.
    ///
    /// Media type only — never the body, which carries user data. This one word separates two
    /// failures that otherwise look identical in the log: an upstream that answered 200 with the
    /// JSON the client wanted, and one that answered 200 with an HTML sign-in or landing page.
    /// The second reads as success everywhere except inside the client, which then reports
    /// something oblique like "response completed without a reply".
    /// </summary>
    private static string DescribeContentType(HttpResponseMessage? response)
    {
        if (response?.Content.Headers.ContentType?.MediaType is not { } mediaType) return "";

        // A JSON or event-stream answer is the expected case and needs no annotation.
        return mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" [{mediaType}]";
    }

    /// <summary>
    /// Puts the token where the route says it goes. Returns false when the request could not
    /// carry it (a body placement on a body this cannot rewrite), so the caller can say so in
    /// the activity log rather than leaving a bare 401 to explain itself.
    /// </summary>
    private async ValueTask<bool> InjectAsync(
        RequestTransformContext context, CredentialInjection injection, string token, string? credentialName)
    {
        var value = injection.FormatValue(token);

        switch (injection.Placement)
        {
            case CredentialPlacement.Query:
                // Assigning replaces any same-named parameter the caller supplied, so a caller
                // cannot pre-set "?access_token=..." and have the upstream see two of them.
                context.Query.Collection[injection.Name] = value;
                return true;

            case CredentialPlacement.Body:
                return await RequestBodyCredentialInjector.TryInjectAsync(
                    context, injection.Name, value, activityLog, credentialName);

            default:
                // Removed first for the same reason: TryAddWithoutValidation appends rather than
                // replaces, so without this a caller-supplied header of the same name would be
                // sent alongside ours and the upstream would pick whichever it liked.
                //
                // Both collections are tried because HttpClient splits headers between them and
                // which one a given name lives in depends on the name, not on the caller.
                TryRemove(context.ProxyRequest.Headers, injection.Name);
                if (context.ProxyRequest.Content is { } content)
                {
                    TryRemove(content.Headers, injection.Name);
                }

                return context.ProxyRequest.Headers.TryAddWithoutValidation(injection.Name, value);
        }
    }

    /// <summary>
    /// Removes a header if that collection is allowed to hold it.
    ///
    /// HttpHeaders.Remove throws "Misused header name" when asked for a name belonging to the
    /// other collection — request headers on an HttpContent, or content headers on an
    /// HttpRequestMessage. That threw straight out of the transform and became a bare 502, which
    /// is how every POST through a header-placement route used to fail: a GET has no Content at
    /// all, so the faulty call was never reached, and only requests with a body hit it. MCP
    /// traffic is entirely POST, so the funnel met it immediately.
    ///
    /// The exception is the answer, not an error: a name this collection cannot hold is a name it
    /// cannot have a stale value under either.
    /// </summary>
    private static void TryRemove(System.Net.Http.Headers.HttpHeaders headers, string name)
    {
        try
        {
            headers.Remove(name);
        }
        catch (InvalidOperationException)
        {
            // Not a header this collection can carry; nothing to clear.
        }
    }

    /// <summary>
    /// Path plus query *keys only*. Activity logs are plaintext on disk beside the encrypted
    /// store and are kept for days, while query strings routinely carry API keys, document
    /// ids, search terms, and email addresses — logging them raw quietly undid much of what
    /// encrypting the store bought. The local API key can also arrive as a query parameter,
    /// which must never be written down.
    /// </summary>
    private static string LogSafePath(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        if (!request.QueryString.HasValue || request.Query.Count == 0)
        {
            return request.Path;
        }

        var keys = string.Join("&", request.Query.Keys.Select(k => $"{k}=<redacted>"));
        return $"{request.Path}?{keys}";
    }
}
