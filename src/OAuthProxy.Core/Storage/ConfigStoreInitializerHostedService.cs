using Microsoft.Extensions.Hosting;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;

namespace OAuthProxy.Core.Storage;

/// <summary>
/// Loads the encrypted config store from disk and does the first YARP route/cluster build
/// before the host starts accepting requests.
/// </summary>
public sealed class ConfigStoreInitializerHostedService(
    ConfigStoreCache configStoreCache,
    SecureStore secureStore,
    ProxyConfigChangeNotifier proxyConfigChangeNotifier,
    ActivityLog activityLog) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        activityLog.Log("STARTUP loading encrypted config store");
        await configStoreCache.InitializeAsync(cancellationToken);

        // The quarantine path was recorded but never read outside tests, so a store that could
        // not be decrypted took every credential, route, and upstream with it in total silence:
        // a fresh API key was generated and configured clients started getting 403 with nothing
        // anywhere to explain it. Say so plainly, and point at the file that was kept.
        if (secureStore.QuarantinedFilePath is { } quarantinedPath)
        {
            activityLog.Log(
                $"STARTUP existing config store could not be read and was renamed to '{quarantinedPath}'. "
                + "Starting with empty configuration — credentials, upstreams, and routes must be set up "
                + "again, and a new local API key has been generated.");
        }

        var store = configStoreCache.Current;
        activityLog.Log($"STARTUP loaded {store.Credentials.Count} credential(s), {store.Upstreams.Count} upstream(s), listening on port {store.Settings.ListenPort}");

        WarnAboutInsecureEndpoints(store);

        proxyConfigChangeNotifier.Rebuild();
    }

    /// <summary>
    /// URL validation only ran when a record was added, so anything stored by a build that
    /// predates it kept working unchecked — a plain-http upstream putting the access token on
    /// the wire in cleartext, or a plain-http token endpoint doing the same for the client
    /// secret and refresh token. Re-checking on load catches those.
    ///
    /// Warn rather than drop: refusing to serve a route the user has been relying on, with no
    /// way to edit it from a store that will not load, is worse than telling them about it.
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
