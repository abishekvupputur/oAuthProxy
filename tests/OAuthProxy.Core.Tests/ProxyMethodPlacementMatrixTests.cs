using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Mcp;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// Every HTTP method crossed with every credential placement, against a real upstream.
///
/// The existing forwarding tests cover each placement once, mostly over GET, and that gap hid a
/// real failure for a long time: header injection cleared any same-named header from the outgoing
/// request *and* from its content, and the content call throws for a request-header name — which
/// only bites when there is a body. Every GET passed; every POST 502'd.
///
/// One example is not coverage for a matrix. This walks the whole grid so a placement that works
/// for one verb and not another cannot pass unnoticed again.
/// </summary>
public class ProxyMethodPlacementMatrixTests : IAsyncLifetime
{
    private const string ApiKey = "matrix-test-key-0123456789";
    private const string Token = "MATRIX-ACCESS-TOKEN";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"oauthproxy-matrix-logs-{Guid.NewGuid()}");

    private WebApplication _upstream = null!;
    private WebApplication _proxy = null!;
    private HttpClient _client = null!;

    private sealed record Seen(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    private readonly List<Seen> _received = [];
    private string? _lastForwarderError;

    /// <summary>Verbs a proxied API realistically sees, including the ones that usually carry no body.</summary>
    public static TheoryData<string> AllMethods()
    {
        var data = new TheoryData<string>();

        foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" })
        {
            data.Add(method);
        }

        return data;
    }

    /// <summary>Every verb crossed with every placement.</summary>
    public static TheoryData<string, CredentialPlacement> MethodPlacementMatrix()
    {
        var data = new TheoryData<string, CredentialPlacement>();

        foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE" })
        {
            foreach (var placement in Enum.GetValues<CredentialPlacement>())
            {
                data.Add(method, placement);
            }
        }

        return data;
    }

    public async Task InitializeAsync()
    {
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        upstreamBuilder.Logging.ClearProviders();
        _upstream = upstreamBuilder.Build();
        _upstream.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            lock (_received)
            {
                _received.Add(new Seen(
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString.Value ?? "",
                    context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    body));
            }

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
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(_logPath)));

        _proxy = proxyBuilder.Build();

        _proxy.Use(async (context, next) =>
        {
            await next();
            if (context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>() is { } error)
            {
                _lastForwarderError = $"{error.Error}: {error.Exception?.Message}";
            }
        });

        // The full pipeline, funnel gate included, so this doubles as proof that adding the
        // funnel did not disturb ordinary proxying.
        _proxy.UseLocalAccessGuard();
        _proxy.UseMcpFunnelGate();
        _proxy.MapMcpFunnel();
        _proxy.MapReverseProxy();
        await _proxy.StartAsync();

        var cache = _proxy.Services.GetRequiredService<ConfigStoreCache>();
        var credential = new CredentialRecord
        {
            Name = "matrix-credential",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };
        var upstreamRecord = new UpstreamRecord { Name = "echo", BaseUrl = upstreamUrl };

        await cache.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstreamRecord);

            // One key value across every route here: these tests are about what reaches the
            // upstream, not about which key opens which route (that is LocalAccessGuardTests).
            void AddRoute(string prefix, CredentialPlacement placement, string name, string valuePrefix) =>
                store.Routes.Add(new RouteMapping
                {
                    PathPrefix = prefix,
                    UpstreamId = upstreamRecord.Id,
                    StripPrefix = true,
                    Key = new ProxyKey { Value = ApiKey },
                    Credentials =
                    [
                        new RouteCredential
                        {
                            CredentialId = credential.Id,
                            Placement = placement,
                            ParameterName = name,
                            ValuePrefix = valuePrefix,
                        },
                    ],
                });

            AddRoute(PrefixFor(CredentialPlacement.Header), CredentialPlacement.Header, "Authorization", "Bearer ");
            AddRoute(PrefixFor(CredentialPlacement.Query), CredentialPlacement.Query, "access_token", "");
            AddRoute(PrefixFor(CredentialPlacement.Body), CredentialPlacement.Body, "access_token", "");
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


        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    private static string PrefixFor(CredentialPlacement placement) => $"/m/{placement.ToString().ToLowerInvariant()}";

    [Theory]
    [MemberData(nameof(MethodPlacementMatrix))]
    public async Task EveryMethodAndPlacementForwardsSuccessfully(string method, CredentialPlacement placement)
    {
        var seen = await SendAsync(method, placement, WithJsonBody(method));

        Assert.Equal(method, seen.Method);
        Assert.Equal("/resource", seen.Path);
    }

    [Theory]
    [MemberData(nameof(MethodPlacementMatrix))]
    public async Task EveryMethodAndPlacementDeliversTheTokenExactlyOnce(string method, CredentialPlacement placement)
    {
        var seen = await SendAsync(method, placement, WithJsonBody(method));

        switch (placement)
        {
            case CredentialPlacement.Header:
                Assert.Equal($"Bearer {Token}", seen.Header("Authorization"));
                Assert.DoesNotContain(Token, seen.Query);
                break;

            case CredentialPlacement.Query:
                Assert.Contains($"access_token={Token}", seen.Query);
                Assert.Null(seen.Header("Authorization"));
                break;

            case CredentialPlacement.Body:
                // Only a request that carries a body can carry a credential in one. A verb sent
                // without content is forwarded unauthenticated rather than being given a body it
                // never had — inventing one would change the request's meaning.
                if (HasBody(method))
                {
                    using var json = JsonDocument.Parse(seen.Body);
                    Assert.Equal(Token, json.RootElement.GetProperty("access_token").GetString());
                }

                Assert.Null(seen.Header("Authorization"));
                break;
        }
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task HeaderPlacementNeverLeavesACallerSuppliedAuthorizationInPlace(string method)
    {
        // The confused-deputy guarantee has to hold for every verb, not just the ones with no
        // body: a caller must never be able to use this proxy as a courier for its own token.
        var request = new HttpRequestMessage(new HttpMethod(method), $"{PrefixFor(CredentialPlacement.Header)}/resource")
        {
            Content = WithJsonBody(method),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);
        request.Headers.Add("Authorization", "Bearer CALLER-SUPPLIED-TOKEN");
        request.Headers.Add("Cookie", "session=caller-session");

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        var seen = Single();
        Assert.Equal($"Bearer {Token}", seen.Header("Authorization"));
        Assert.Null(seen.Header("Cookie"));
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task TheLocalApiKeyIsStrippedForEveryMethod(string method)
    {
        var seen = await SendAsync(method, CredentialPlacement.Header, WithJsonBody(method));

        Assert.Null(seen.Header(LocalAccessGuard.ApiKeyHeaderName));
        Assert.DoesNotContain(ApiKey, seen.Query);
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task EveryMethodIsRefusedWithoutTheLocalApiKey(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"{PrefixFor(CredentialPlacement.Header)}/resource")
        {
            Content = WithJsonBody(method),
        };

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_received);
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task TheApiKeyMayAlsoArriveInTheQueryForEveryMethod(string method)
    {
        // The fallback that exists for clients which cannot set headers — browser EventSource,
        // and the SSE transports some MCP clients still use.
        var request = new HttpRequestMessage(
            new HttpMethod(method),
            $"{PrefixFor(CredentialPlacement.Header)}/resource?page=2&{LocalAccessGuard.ApiKeyQueryName}={ApiKey}")
        {
            Content = WithJsonBody(method),
        };

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        var seen = Single();
        Assert.DoesNotContain(ApiKey, seen.Query);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, seen.Query);
        Assert.Contains("page=2", seen.Query);
    }

    [Theory]
    [MemberData(nameof(MethodPlacementMatrix))]
    public async Task ACallerSuppliedQueryStringSurvivesEveryPlacement(string method, CredentialPlacement placement)
    {
        var request = new HttpRequestMessage(
            new HttpMethod(method), $"{PrefixFor(placement)}/resource?page=2&filter=open")
        {
            Content = WithJsonBody(method),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        var seen = Single();
        Assert.Contains("page=2", seen.Query);
        Assert.Contains("filter=open", seen.Query);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task BodyPlacementKeepsAFormBodyIntactForEveryWritingMethod(string method)
    {
        var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant", "value"),
            new KeyValuePair<string, string>("other", "thing"),
        ]);

        var seen = await SendAsync(method, CredentialPlacement.Body, content);

        Assert.Contains("grant=value", seen.Body);
        Assert.Contains("other=thing", seen.Body);
        Assert.Contains($"access_token={Token}", seen.Body);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task BodyPlacementLeavesAnUnparseableBodyByteForByte(string method)
    {
        // Half-rewriting a body is worse than forwarding it unauthenticated: the upstream would
        // receive something the caller never sent.
        const string plain = "not a structured body";

        var seen = await SendAsync(
            method, CredentialPlacement.Body, new StringContent(plain, Encoding.UTF8, "text/plain"));

        Assert.Equal(plain, seen.Body);
        Assert.DoesNotContain(Token, seen.Body);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task BodyPlacementWorksOnAChunkedBodyWithNoContentLength(string method)
    {
        // A chunked body used to be refused outright, which meant body placement silently never
        // authenticated MCP traffic: every MCP client streams its JSON-RPC POSTs this way. The
        // activity log said "body has no Content-Length (chunked or streamed)" and the upstream
        // got an unauthenticated request.
        var seen = await SendAsync(method, CredentialPlacement.Body, ChunkedJson("""{"jsonrpc":"2.0","method":"ping"}"""));

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(Token, json.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("ping", json.RootElement.GetProperty("method").GetString());

        // Rewritten with a known length, so the chunked framing must be gone — the two together
        // are illegal and the upstream would read a chunk-size line as body content.
        Assert.Equal(seen.Body.Length.ToString(), seen.Header("Content-Length"));
        Assert.Null(seen.Header("Transfer-Encoding"));
    }

    [Fact]
    public async Task AChunkedBodyTooLargeToBufferIsStillForwardedWhole()
    {
        // The other half of accepting undeclared bodies: on discovering mid-read that a body is
        // too large, the bytes already consumed have to be put back in front of the remainder.
        // Getting this wrong truncates uploads rather than merely failing to authenticate them.
        var payload = new string('x', 2 * 1024 * 1024);
        var content = ChunkedJson($$"""{"blob":"{{payload}}"}""");

        var seen = await SendAsync("POST", CredentialPlacement.Body, content);

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(payload, json.RootElement.GetProperty("blob").GetString());
        Assert.False(json.RootElement.TryGetProperty("access_token", out _));
    }

    /// <summary>
    /// Content that reports no length, so it goes on the wire chunked — the shape every MCP
    /// client produces.
    /// </summary>
    private static HttpContent ChunkedJson(string body)
    {
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        return content;
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task ARewrittenBodyArrivesWithAMatchingContentLength(string method)
    {
        // A stale Content-Length either truncates the JSON or leaves the upstream waiting for
        // bytes that never come — both look like a hung or corrupt upstream, not a proxy bug.
        var seen = await SendAsync(
            method, CredentialPlacement.Body,
            new StringContent("""{"jsonrpc":"2.0","method":"ping"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(seen.Body.Length.ToString(), seen.Header("Content-Length"));
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task NoMethodCanClimbOutOfItsRoutePrefix(string method)
    {
        var request = new HttpRequestMessage(
            new HttpMethod(method), $"{PrefixFor(CredentialPlacement.Header)}/../escaped")
        {
            Content = WithJsonBody(method),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(_received);
    }

    /// <summary>Bodies are sent on the verbs that carry them, and omitted on the ones that do not.</summary>
    private static bool HasBody(string method) => method is "POST" or "PUT" or "PATCH";

    private static HttpContent? WithJsonBody(string method) => HasBody(method)
        ? new StringContent("""{"field":"value"}""", Encoding.UTF8, "application/json")
        : null;

    private async Task<Seen> SendAsync(string method, CredentialPlacement placement, HttpContent? content)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"{PrefixFor(placement)}/resource")
        {
            Content = content,
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode,
            $"{method} with {placement} placement: {response.StatusCode} / {_lastForwarderError}");

        return Single();
    }

    private Seen Single()
    {
        lock (_received)
        {
            return Assert.Single(_received);
        }
    }
}
