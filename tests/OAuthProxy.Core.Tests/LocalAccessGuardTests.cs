using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// These cover the single control standing between any local process and the user's OAuth
/// tokens, so each rejection path gets its own test.
/// </summary>
public class LocalAccessGuardTests : IAsyncLifetime
{
    private const string ValidKey = "test-key-abcdefghijklmnopqrstuvwxyz";

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<ActivityLog>(_ =>
                            new ActivityLog(Path.Combine(Path.GetTempPath(), $"oauthproxy-test-logs-{Guid.NewGuid()}")));
                        services.AddSingleton(_ => new SecureStore(
                            Path.Combine(Path.GetTempPath(), $"oauthproxy-test-{Guid.NewGuid()}.dat")));
                        services.AddSingleton<ConfigStoreCache>();
                    })
                    .Configure(app =>
                    {
                        var cache = app.ApplicationServices.GetRequiredService<ConfigStoreCache>();
                        cache.Current.Settings.LocalApiKey = ValidKey;

                        app.UseLocalAccessGuard();
                        app.Run(async context =>
                        {
                            // Stand-in for a proxied upstream that answers with permissive CORS.
                            context.Response.Headers["Access-Control-Allow-Origin"] = "*";

                            // Echoed back so tests can assert on exactly what an upstream would
                            // have received in its own logs.
                            context.Response.Headers["X-Echo-Query"] = context.Request.QueryString.Value ?? "";
                            context.Response.Headers["X-Echo-Had-Key-Header"] =
                                context.Request.Headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName).ToString();
                            await context.Response.WriteAsync("upstream-payload");
                        });
                    });
            })
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task ValidKeyInHeader_IsAllowedThrough()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream-payload", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidKeyInQueryString_IsAllowedThrough()
    {
        // Supported because browser EventSource, used by some MCP SSE transports, cannot set
        // request headers at all.
        var response = await _client.GetAsync(
            $"http://127.0.0.1/anything?{LocalAccessGuard.ApiKeyQueryName}={ValidKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyInQueryString_IsNotForwardedUpstream()
    {
        // It authenticates the caller to this proxy and nothing else. Forwarded, it would land
        // in the upstream's access log — handing a third party the key to the local proxy.
        var response = await _client.GetAsync(
            $"http://127.0.0.1/anything?{LocalAccessGuard.ApiKeyQueryName}={ValidKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwardedQuery = response.Headers.GetValues("X-Echo-Query").Single();
        Assert.DoesNotContain(ValidKey, forwardedQuery);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, forwardedQuery);
    }

    [Fact]
    public async Task OtherQueryParameters_SurviveUntouched()
    {
        // The caller's own parameters are the whole point of the request; only proxy_key goes.
        var response = await _client.GetAsync(
            $"http://127.0.0.1/anything?token=abc&{LocalAccessGuard.ApiKeyQueryName}={ValidKey}&page=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwardedQuery = response.Headers.GetValues("X-Echo-Query").Single();
        Assert.Contains("token=abc", forwardedQuery);
        Assert.Contains("page=2", forwardedQuery);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, forwardedQuery);
    }

    [Fact]
    public async Task ApiKeyInHeader_IsNotForwardedUpstream()
    {
        // Same reasoning as the query-string case: YARP copies request headers through by
        // default, so without an explicit removal the upstream receives the key to this proxy.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything?token=abc");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal("False", response.Headers.GetValues("X-Echo-Had-Key-Header").Single());

        // ...and the caller's own parameters are still untouched.
        Assert.Equal("?token=abc", response.Headers.GetValues("X-Echo-Query").Single());
    }

    [Fact]
    public async Task ApiKeyHeader_IsStrippedEvenWhenTheRequestIsRejected()
    {
        // A rejected request never reaches an upstream, but the header must not survive into
        // anything downstream either — the removal is unconditional rather than allow-path only.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, "wrong-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingKey_IsRejected()
    {
        // The core confused-deputy case: any process that merely knows the port.
        var response = await _client.GetAsync("http://127.0.0.1/anything");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WrongKey_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, "not-the-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NonLoopbackHostHeader_IsRejectedEvenWithAValidKey()
    {
        // DNS rebinding: evil.com re-resolves to 127.0.0.1, so the browser treats the response
        // as same-origin and lets attacker JavaScript read it.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://evil.com/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RequestWithOriginHeader_IsRejectedEvenWithAValidKey()
    {
        // Only browsers send Origin, and no legitimate local client is a browser page.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);
        request.Headers.Add("Origin", "https://evil.com");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PermissiveCorsHeadersFromUpstream_AreStripped()
    {
        // YARP copies response headers verbatim; an upstream sending "*" would otherwise let
        // any web page read proxied responses directly.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(response.Headers, h =>
            h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }
}
