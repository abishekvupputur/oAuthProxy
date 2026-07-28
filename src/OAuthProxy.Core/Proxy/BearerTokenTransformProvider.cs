using System.Net.Http.Headers;
using OAuthProxy.Core.Auth;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Storage;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace OAuthProxy.Core.Proxy;

/// <summary>
/// Injects "Authorization: Bearer &lt;token&gt;" into proxied requests, reading the token from
/// ConfigStoreCache live on every request (not captured at route-build time). This is what
/// decouples token refresh from route rebuilds — a refreshed token applies to the very next
/// proxied request automatically, no config reload needed.
/// </summary>
public sealed class BearerTokenTransformProvider(
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

        context.AddRequestTransform(async transformContext =>
        {
            var credential = configStoreCache.GetCredential(credentialId);

            // Not credential.Token.AccessToken directly: this refreshes first if the token has
            // already expired, so a request arriving between refresh-loop ticks (or after the
            // machine slept through one) still goes out authenticated instead of 401-ing.
            var token = await accessTokenProvider.GetAccessTokenAsync(
                credentialId, transformContext.HttpContext.RequestAborted);

            // Assigned unconditionally, including the null case. YARP copies request headers
            // through by default, so merely *skipping* the assignment when we have no token
            // would forward the caller's own Authorization header to the upstream — letting a
            // local caller use this proxy to pass arbitrary credentials to a configured host.
            transformContext.ProxyRequest.Headers.Authorization =
                token is null ? null : new AuthenticationHeaderValue("Bearer", token);

            // Same reasoning for cookies: the upstream's auth is the bearer token we attach,
            // never ambient browser state, and forwarding cookies only risks carrying a
            // session the caller should not have been able to spend.
            transformContext.ProxyRequest.Headers.Remove("Cookie");

            var request = transformContext.HttpContext.Request;
            activityLog.Log(token is not null
                ? $"PROXY {request.Method} {LogSafePath(request)} -> {transformContext.DestinationPrefix} [token: {credential?.Name}]"
                : $"PROXY {request.Method} {LogSafePath(request)} -> {transformContext.DestinationPrefix} [NO TOKEN - credential not connected]");
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

            activityLog.Log($"  <- {(status is null ? "no response (upstream unreachable)" : ((int)status).ToString())} for {request.Method} {LogSafePath(request)}");
            return ValueTask.CompletedTask;
        });
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
