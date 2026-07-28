using OAuthProxy.Core.Models;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace OAuthProxy.Core.Proxy;

/// <summary>
/// Pure translation from user-configured RouteMapping/UpstreamRecord into YARP's route/cluster
/// config. One cluster per route (1:1) — traffic volume for a personal tool is trivial, no
/// need to dedupe clusters by upstream.
/// </summary>
public static class ProxyConfigBuilder
{
    public const string CredentialIdMetadataKey = "CredentialId";

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(
        IReadOnlyList<RouteMapping> routeMappings,
        IReadOnlyList<UpstreamRecord> upstreams)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        foreach (var mapping in routeMappings)
        {
            if (!mapping.Enabled) continue;

            var upstream = upstreams.FirstOrDefault(u => u.Id == mapping.UpstreamId);
            if (upstream is null) continue;

            // Skipped here rather than handed to YARP: an unparseable route template makes YARP
            // reject the whole config update and keep the previous one, so a single bad prefix
            // would take every *other* route's pending edit down with it. Dropping it means the
            // rest still apply and the count this method returns is the count that took effect.
            // Newly created prefixes are already blocked in the UI; this catches stores written
            // before that check existed.
            if (!RouteValidation.IsValidPathPrefix(mapping.PathPrefix)) continue;

            var routeId = mapping.Id.ToString();
            var prefix = mapping.PathPrefix.TrimEnd('/');

            routes.Add(new RouteConfig
            {
                RouteId = routeId,
                ClusterId = routeId,
                Match = new RouteMatch
                {
                    Path = $"{prefix}/{{**catch-all}}",
                },
                Metadata = new Dictionary<string, string>
                {
                    [CredentialIdMetadataKey] = mapping.CredentialId.ToString(),
                },
                Transforms = mapping.StripPrefix
                    ? [new Dictionary<string, string> { ["PathRemovePrefix"] = prefix }]
                    : null,
            });

            clusters.Add(new ClusterConfig
            {
                ClusterId = routeId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["d1"] = new DestinationConfig { Address = upstream.BaseUrl },
                },
                HttpRequest = new ForwarderRequestConfig
                {
                    // Generous timeout so long-lived MCP SSE streams aren't cut off
                    // by YARP's default 100s activity timeout.
                    ActivityTimeout = TimeSpan.FromMinutes(30),
                },
            });
        }

        return (routes, clusters);
    }
}
