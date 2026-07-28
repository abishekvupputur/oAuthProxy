using System.Net.Http.Headers;
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
public sealed class BearerTokenTransformProvider(ConfigStoreCache configStoreCache, ActivityLog activityLog) : ITransformProvider
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

        context.AddRequestTransform(transformContext =>
        {
            var credential = configStoreCache.GetCredential(credentialId);
            var token = credential?.Token?.AccessToken;
            if (token is not null)
            {
                transformContext.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var request = transformContext.HttpContext.Request;
            activityLog.Log(token is not null
                ? $"PROXY {request.Method} {request.Path}{request.QueryString} -> {transformContext.DestinationPrefix} [token: {credential?.Name}]"
                : $"PROXY {request.Method} {request.Path}{request.QueryString} -> {transformContext.DestinationPrefix} [NO TOKEN - credential not connected]");

            return ValueTask.CompletedTask;
        });

        context.AddResponseTransform(transformContext =>
        {
            var status = transformContext.ProxyResponse?.StatusCode;
            var request = transformContext.HttpContext.Request;
            activityLog.Log($"  <- {(status is null ? "no response (upstream unreachable)" : ((int)status).ToString())} for {request.Method} {request.Path}");
            return ValueTask.CompletedTask;
        });
    }
}
