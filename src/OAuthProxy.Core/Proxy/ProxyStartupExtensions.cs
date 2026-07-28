using Microsoft.Extensions.DependencyInjection;
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
    /// and the bearer-token transform.
    /// </summary>
    public static IServiceCollection AddOAuthProxy(this IServiceCollection services)
    {
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<SecureStore>();
        services.AddSingleton<ConfigStoreCache>();
        services.AddHostedService<ConfigStoreInitializerHostedService>();

        services.AddSingleton<GoogleOAuthService>();
        services.AddSingleton<OAuth2Service>();
        services.AddHostedService<TokenRefreshService>();

        var configProvider = new InMemoryConfigProvider([], []);
        services.AddSingleton(configProvider);
        services.AddSingleton<IProxyConfigProvider>(configProvider);

        services.AddSingleton<ProxyConfigChangeNotifier>();
        services.AddSingleton<ITransformProvider, BearerTokenTransformProvider>();

        services.AddReverseProxy();

        return services;
    }
}
