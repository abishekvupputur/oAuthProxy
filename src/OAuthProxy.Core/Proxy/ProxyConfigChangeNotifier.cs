using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Storage;
using Yarp.ReverseProxy.Configuration;

namespace OAuthProxy.Core.Proxy;

/// <summary>
/// Rebuilds and hot-applies YARP's route/cluster config from the current ConfigStoreCache
/// state. Call after any edit to Routes/Upstreams (or on initial load).
/// </summary>
public sealed class ProxyConfigChangeNotifier(
    ConfigStoreCache configStoreCache,
    InMemoryConfigProvider configProvider,
    ActivityLog activityLog)
{
    public void Rebuild()
    {
        var store = configStoreCache.Current;
        var (routes, clusters) = ProxyConfigBuilder.Build(store.Routes, store.Upstreams);
        configProvider.Update(routes, clusters);
        activityLog.Log($"ROUTES reloaded — {routes.Count} active route(s)");
        foreach (var route in routes)
        {
            activityLog.Log($"  {route.Match.Path} -> {clusters.First(c => c.ClusterId == route.ClusterId).Destinations!["d1"].Address}");
        }
    }
}
