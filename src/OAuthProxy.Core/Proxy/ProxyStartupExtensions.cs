using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using OAuthProxy.Core.Auth;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Mcp;
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
        services.AddMcpFunnel();

        return services;
    }

    /// <summary>
    /// Registers the MCP funnel: the upstream session pool, the per-funnel handler factory, and
    /// an MCP server whose behaviour is chosen per request from the slug in the path.
    ///
    /// Stateless is the load-bearing setting. In stateless mode ConfigureSessionOptions runs on
    /// every HTTP request rather than once when a session is created, so a funnel edited in the
    /// GUI takes effect on the agent's next call — no session to invalidate, no list_changed
    /// notification to plumb, and no session affinity to preserve. The cost is that the funnel
    /// endpoint cannot offer sampling, elicitation, or resource subscriptions, none of which a
    /// tool-shaping proxy needs. Upstream sessions are stateful and pooled regardless.
    /// </summary>
    private static IServiceCollection AddMcpFunnel(this IServiceCollection services)
    {
        services.AddSingleton<McpSourceConnectionPool>();
        services.AddSingleton<McpCatalogCache>();
        services.AddSingleton<McpFunnelHandlerFactory>();

        services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;

                options.ConfigureSessionOptions = (httpContext, serverOptions, _) =>
                {
                    var handlerFactory = httpContext.RequestServices.GetRequiredService<McpFunnelHandlerFactory>();

                    // The gate has already refused unknown slugs, so a miss here means the funnel
                    // was deleted between the two — answer as an empty server rather than throw.
                    var slug = httpContext.Request.RouteValues[McpFunnelEndpoints.SlugRouteValue]?.ToString();
                    if (handlerFactory.FindFunnel(slug) is not { } funnel) return Task.CompletedTask;

                    serverOptions.ServerInfo = new Implementation
                    {
                        Name = $"OAuthProxy funnel: {funnel.Name}",
                        Version = typeof(ProxyStartupExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    };

                    // Declared unconditionally. A funnel's sources can gain or lose prompts and
                    // resources at any time, and capabilities are negotiated once per request —
                    // advertising only what happens to exist right now would make a client that
                    // connected a moment earlier believe the funnel can never offer them.
                    serverOptions.Capabilities = new ServerCapabilities
                    {
                        Tools = new ToolsCapability(),
                        Resources = new ResourcesCapability(),
                        Prompts = new PromptsCapability(),
                    };

                    serverOptions.Handlers = handlerFactory.Create(funnel.Id, funnel.Name);

                    return Task.CompletedTask;
                };
            });

        return services;
    }
}
