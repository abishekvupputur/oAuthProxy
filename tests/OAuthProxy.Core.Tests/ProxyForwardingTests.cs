using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// End-to-end through the real YARP forwarder, against a real upstream on a real socket.
///
/// LocalAccessGuardTests stops at the middleware, which cannot prove what YARP actually
/// forwards: the guard removes the API key from HttpContext.Request, and whether that removal
/// is visible to the forwarder depends on YARP reading the live request rather than a snapshot
/// taken earlier in the pipeline. The only way to know is to look at what the upstream sees.
/// </summary>
public class ProxyForwardingTests : IAsyncLifetime
{
    private const string ApiKey = "forwarding-test-key-0123456789";

    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"oauthproxy-fwd-{Guid.NewGuid()}.dat");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"oauthproxy-fwd-logs-{Guid.NewGuid()}");

    private WebApplication _upstream = null!;
    private WebApplication _proxy = null!;
    private HttpClient _client = null!;

    /// <summary>What the upstream saw on the most recent request.</summary>
    private static readonly List<(string Path, string Query, string? ProxyKeyHeader, string? Authorization, string? Cookie)> Received = [];

    public async Task InitializeAsync()
    {
        Received.Clear();

        // A genuine upstream listening on a loopback port, recording exactly what arrives.
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        upstreamBuilder.Logging.ClearProviders();
        _upstream = upstreamBuilder.Build();
        _upstream.Run(async context =>
        {
            Received.Add((
                context.Request.Path,
                context.Request.QueryString.Value ?? "",
                context.Request.Headers[LocalAccessGuard.ApiKeyHeaderName].FirstOrDefault(),
                context.Request.Headers.Authorization.FirstOrDefault(),
                context.Request.Headers.Cookie.FirstOrDefault()));
            await context.Response.WriteAsync("upstream-ok");
        });
        await _upstream.StartAsync();

        var upstreamUrl = _upstream.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        var proxyBuilder = WebApplication.CreateBuilder();
        proxyBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        proxyBuilder.Logging.ClearProviders();
        proxyBuilder.Services.AddOAuthProxy();

        // Redirect storage and logs to temp paths so the test cannot touch the real
        // %APPDATA%\OAuthProxy store belonging to whoever runs the suite.
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton(_ => new SecureStore(_storePath)));
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(_logPath)));

        _proxy = proxyBuilder.Build();
        _proxy.UseLocalAccessGuard();
        _proxy.MapReverseProxy();
        await _proxy.StartAsync();

        // Configure a route now that the hosted-service initialization has run.
        var cache = _proxy.Services.GetRequiredService<ConfigStoreCache>();
        var credential = new CredentialRecord
        {
            Name = "test-credential",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet("UPSTREAM-ACCESS-TOKEN", "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };
        var upstreamRecord = new UpstreamRecord { Name = "echo", BaseUrl = upstreamUrl };

        await cache.MutateAsync(store =>
        {
            store.Settings.LocalApiKey = ApiKey;
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstreamRecord);
            store.Routes.Add(new RouteMapping
            {
                PathPrefix = "/app/echo",
                UpstreamId = upstreamRecord.Id,
                CredentialId = credential.Id,
                StripPrefix = true,
            });
        });
        _proxy.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

        var proxyUrl = _proxy.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        _client = new HttpClient { BaseAddress = new Uri(proxyUrl) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _proxy.StopAsync();
        await _upstream.StopAsync();
        foreach (var path in new[] { _storePath, _storePath + ".tmp" })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task KeyInHeader_ReachesUpstreamStrippedAndTokenInjected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/echo/resource?token=abc");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(Received);
        Assert.Null(seen.ProxyKeyHeader);
        Assert.Equal("/resource", seen.Path);
        Assert.Equal("?token=abc", seen.Query);
        Assert.Equal("Bearer UPSTREAM-ACCESS-TOKEN", seen.Authorization);
    }

    [Fact]
    public async Task KeyInQuery_ReachesUpstreamStripped()
    {
        var response = await _client.GetAsync(
            $"/app/echo/resource?token=abc&{LocalAccessGuard.ApiKeyQueryName}={ApiKey}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(Received);
        Assert.DoesNotContain(ApiKey, seen.Query);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, seen.Query);
        Assert.Equal("?token=abc", seen.Query);
    }

    [Fact]
    public async Task CallerSuppliedAuthorizationAndCookies_AreNotPassedThrough()
    {
        // A caller must not be able to use the proxy as a courier for its own credentials;
        // the only Authorization the upstream sees is the one this app attaches.
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/echo/resource");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "CALLER-SUPPLIED-TOKEN");
        request.Headers.Add("Cookie", "session=caller-session");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(Received);
        Assert.Equal("Bearer UPSTREAM-ACCESS-TOKEN", seen.Authorization);
        Assert.Null(seen.Cookie);
    }

    [Fact]
    public async Task RejectedRequest_NeverReachesTheUpstream()
    {
        var response = await _client.GetAsync("/app/echo/resource");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Received);
    }

    [Fact]
    public async Task EncodedDotSegments_CannotClimbOutOfTheRoutePrefix()
    {
        // Sent over a raw socket on purpose: System.Uri decodes "%2e" to "." and removes the
        // dot segments client-side, so HttpClient physically cannot put this on the wire.
        // Anything speaking HTTP directly (curl --path-as-is, a socket) has no such difficulty.
        //
        // Kestrel percent-decodes and *then* removes dot segments, so this arrives at routing
        // as "/escaped", matches no route, and never reaches an upstream. Pinned as a test
        // because the whole confinement story rests on it: if that normalization order ever
        // changed, a caller could climb above an upstream's base path with the user's token
        // attached and nothing else in the pipeline would notice.
        var statusLine = await SendRawAsync("GET /app/echo/%2e%2e/%2e%2e/escaped HTTP/1.1");

        Assert.DoesNotContain("200", statusLine);
        Assert.Empty(Received);
    }

    [Fact]
    public async Task EncodedDotSegments_AreResolvedBeforeTheRequestIsForwarded()
    {
        // The other half of the same behavior, and the part that proves it is normalization
        // rather than a coincidental 404: a ".." that resolves back inside the prefix is
        // forwarded, with the upstream seeing the already-collapsed path.
        var statusLine = await SendRawAsync("GET /app/echo/sub/%2e%2e/resource HTTP/1.1");

        Assert.Contains("200", statusLine);
        Assert.Equal("/resource", Assert.Single(Received).Path);
    }

    [Fact]
    public async Task RawRequestWithoutDotSegments_StillWorks()
    {
        // Guards against the raw-socket helper above passing for the wrong reason.
        var statusLine = await SendRawAsync("GET /app/echo/resource HTTP/1.1");

        Assert.Contains("200", statusLine);
        Assert.Equal("/resource", Assert.Single(Received).Path);
    }

    /// <summary>
    /// Sends a request line verbatim, with no client-side URI normalization, and returns the
    /// response's status line.
    /// </summary>
    private async Task<string> SendRawAsync(string requestLine)
    {
        var address = _client.BaseAddress!;

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port);

        await using var stream = tcp.GetStream();
        var raw = $"{requestLine}\r\n"
                  + $"Host: {address.Host}:{address.Port}\r\n"
                  + $"{LocalAccessGuard.ApiKeyHeaderName}: {ApiKey}\r\n"
                  + "Connection: close\r\n\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(raw));

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        return await reader.ReadLineAsync() ?? "";
    }

    [Fact]
    public async Task TwoDotsInsideASegment_AreStillForwarded()
    {
        // Only whole ".." segments are traversal. A file legitimately named "a..b" is not, and
        // rejecting it would break real upstream URLs.
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/echo/files/a..b");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/files/a..b", Assert.Single(Received).Path);
    }
}
