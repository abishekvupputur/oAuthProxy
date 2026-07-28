using Microsoft.Extensions.Hosting;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Proxy;

namespace OAuthProxy.Core.Storage;

/// <summary>
/// Loads the encrypted config store from disk and does the first YARP route/cluster build
/// before the host starts accepting requests.
/// </summary>
public sealed class ConfigStoreInitializerHostedService(
    ConfigStoreCache configStoreCache,
    ProxyConfigChangeNotifier proxyConfigChangeNotifier,
    ActivityLog activityLog) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        activityLog.Log("STARTUP loading encrypted config store");
        await configStoreCache.InitializeAsync(cancellationToken);

        var store = configStoreCache.Current;
        activityLog.Log($"STARTUP loaded {store.Credentials.Count} credential(s), {store.Upstreams.Count} upstream(s), listening on port {store.Settings.ListenPort}");

        proxyConfigChangeNotifier.Rebuild();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        activityLog.Log("SHUTDOWN proxy stopping");
        return Task.CompletedTask;
    }
}
