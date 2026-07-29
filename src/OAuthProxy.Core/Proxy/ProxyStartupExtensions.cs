using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OAuthProxy.Core.Auth;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Storage;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms.Builder;

namespace OAuthProxy.Core.Proxy;

public static class ProxyStartupExtensions
{
    /// <summary>
    /// Wires up storage, YARP (with an empty initial config — the real routes get loaded once
    /// ConfigStoreCache finishes loading from disk, via ConfigStoreInitializerHostedService),
    /// and the credential-injection transform.
    /// </summary>
    public static IServiceCollection AddOAuthProxy(this IServiceCollection services)
    {
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<SecureStore>();
        services.AddSingleton<ConfigStoreCache>();
        services.AddHostedService<ConfigStoreInitializerHostedService>();

        services.AddSingleton<GoogleOAuthService>();
        services.AddSingleton<OAuth2Service>();
        services.AddSingleton<AccessTokenProvider>();

        // Registered as a singleton first, then handed to the hosting layer, so the UI can
        // resolve the same instance and clear a credential's retry backoff after a manual
        // reconnect. AddHostedService<T>() alone would create an instance the UI cannot reach.
        services.AddSingleton<TokenRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<TokenRefreshService>());

        // A crashing background service defaults to tearing down the entire host. For an
        // always-on tray app that is the worst outcome: Kestrel stops, every proxied request
        // starts failing, and the tray icon sits there looking healthy. The refresh loop now
        // handles its own errors per tick, and this makes sure any gap in that cannot take
        // the proxy with it.
        services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        var configProvider = new InMemoryConfigProvider([], []);
        services.AddSingleton(configProvider);
        services.AddSingleton<IProxyConfigProvider>(configProvider);

        services.AddSingleton<ProxyConfigChangeNotifier>();
        services.AddSingleton<ITransformProvider, CredentialInjectionTransformProvider>();

        services.AddReverseProxy();

        return services;
    }
}
