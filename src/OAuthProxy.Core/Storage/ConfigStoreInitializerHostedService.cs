using Microsoft.Extensions.Hosting;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Storage;

/// <summary>
/// Loads the config store from the password manager and does the first YARP route/cluster build
/// before the host starts accepting requests.
/// </summary>
public sealed class ConfigStoreInitializerHostedService(
    ConfigStoreCache configStoreCache,
    IConfigVault vault,
    ProxyConfigChangeNotifier proxyConfigChangeNotifier,
    ActivityLog activityLog) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        activityLog.Log("STARTUP loading config store from the password manager");
        await configStoreCache.InitializeAsync(cancellationToken);

        // A load that came back incomplete — a credential whose secret item is missing, say —
        // otherwise looks identical to a working one until a request fails against the upstream
        // hours later. Say so at startup instead.
        if (vault.LastLoadWarning is { } warning)
        {
            activityLog.Log($"STARTUP {warning}");
        }

        var store = configStoreCache.Current;
        activityLog.Log($"STARTUP loaded {store.Credentials.Count} credential(s), {store.Upstreams.Count} upstream(s), listening on port {store.Settings.ListenPort}");

        WarnAboutInsecureEndpoints(store);

        proxyConfigChangeNotifier.Rebuild();
    }

    /// <summary>
    /// URL validation runs when a record is added, but the store is no longer only written by
    /// this app — every record is an item the user can edit directly in their password manager.
    /// Re-checking on load catches what that lets through: a plain-http upstream putting the
    /// access token on the wire in cleartext, or a plain-http token endpoint doing the same for
    /// the client secret and refresh token.
    ///
    /// Warn rather than drop: refusing to serve a route the user has been relying on, over a URL
    /// they can fix in ten seconds, is worse than telling them about it.
    /// </summary>
    private void WarnAboutInsecureEndpoints(ConfigStore store)
    {
        foreach (var upstream in store.Upstreams)
        {
            if (UrlValidation.ValidateEndpoint(upstream.BaseUrl, "Upstream base URL") is { } error)
            {
                activityLog.Log($"STARTUP WARNING upstream '{upstream.Name}': {error}");
            }
        }

        foreach (var credential in store.Credentials)
        {
            var error = UrlValidation.ValidateEndpoint(credential.Authority, "Authority")
                        ?? UrlValidation.ValidateEndpoint(credential.AuthorizationEndpoint, "Authorization endpoint")
                        ?? UrlValidation.ValidateEndpoint(credential.TokenEndpoint, "Token endpoint");

            if (error is not null)
            {
                activityLog.Log($"STARTUP WARNING credential '{credential.Name}': {error}");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        activityLog.Log("SHUTDOWN proxy stopping");
        return Task.CompletedTask;
    }
}
