using System.Text.Json;
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
    /// <summary>
    /// Route metadata key holding the route's whole credential list, JSON-encoded.
    ///
    /// One key rather than a set of scalar keys, because the list is variable-length and YARP's
    /// metadata is a flat string dictionary. The key is always written — for a route with no
    /// credentials its value is "[]" — so the transform provider can tell a route this builder
    /// produced from one it did not, and still install the transform that strips the caller's
    /// own Authorization and cookies.
    /// </summary>
    public const string CredentialsMetadataKey = "Credentials";

    private static readonly JsonSerializerOptions MetadataJson = new() { WriteIndented = false };

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
            // Newly created prefixes are already blocked in the UI; this catches a route edited
            // straight into the vault from the password manager, which bypasses that check.
            if (!RouteValidation.IsValidPathPrefix(mapping.PathPrefix)) continue;

            // Same fail-closed treatment for the credential list. A header name that is not a
            // legal HTTP token, a prefix carrying a newline, or two credentials fighting over one
            // slot cannot be put on the wire as configured — serving the route anyway would send
            // something other than what the UI shows, which looks like a working route that
            // quietly authenticates wrongly.
            var credentials = mapping.Credentials;
            if (!RouteValidation.IsValidCredentialSet(credentials)) continue;

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
                    [CredentialsMetadataKey] = WriteCredentials(credentials),
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

    /// <summary>
    /// Encodes a route's credential list for route metadata. Names are trimmed on the way in so
    /// the transform never has to guess whether a stray space belongs to a header name.
    /// </summary>
    public static string WriteCredentials(IReadOnlyList<RouteCredential> credentials) =>
        JsonSerializer.Serialize(
            credentials.Select(c => new RouteCredential
            {
                CredentialId = c.CredentialId,
                Placement = c.Placement,
                ParameterName = c.ParameterName.Trim(),
                ValuePrefix = c.ValuePrefix,
            }).ToList(),
            MetadataJson);

    /// <summary>
    /// The inverse of what <see cref="Build"/> writes into route metadata. A route with no
    /// credential key, or an unreadable one, yields an empty list — meaning "attach nothing",
    /// which is the only safe reading of metadata this build cannot interpret.
    /// </summary>
    public static IReadOnlyList<RouteCredential> ReadCredentials(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null ||
            !metadata.TryGetValue(CredentialsMetadataKey, out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<RouteCredential>>(json, MetadataJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
