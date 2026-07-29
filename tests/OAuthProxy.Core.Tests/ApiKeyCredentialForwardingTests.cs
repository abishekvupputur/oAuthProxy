using System.Text;
using System.Text.Json;
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
/// Static API keys forwarded through the real pipeline, alone and alongside OAuth tokens.
///
/// A key takes a different path to the wire than a token: there is no expiry to check, no
/// refresh to attempt, and no provider to ask, so <see cref="OAuthProxy.Core.Auth.AccessTokenProvider"/>
/// short-circuits before all of that. Only the upstream's view proves the short-circuit still
/// hands the transform the right value.
///
/// The newline case is the one that matters most. An OAuth token is structurally constrained by
/// the provider that issued it; a key is whatever someone pasted, and a CR or LF written into a
/// header ends the header line and lets the rest be read as further headers.
/// </summary>
public class ApiKeyCredentialForwardingTests : IAsyncLifetime
{
    private const string ProxyKey = "api-key-fwd-test-key-0123456789";
    private const string StaticKey = "STATIC-API-KEY";
    private const string OAuthToken = "OAUTH-ACCESS-TOKEN";

    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"oauthproxy-apikey-{Guid.NewGuid()}.dat");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"oauthproxy-apikey-logs-{Guid.NewGuid()}");

    private WebApplication _upstream = null!;
    private WebApplication _proxy = null!;
    private HttpClient _client = null!;

    private sealed record Seen(string Query, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    private readonly List<Seen> _received = [];
    private string? _lastForwarderError;

    private const string KeyInHeader = "/k/header";
    private const string KeyInQuery = "/k/query";
    private const string KeyInBody = "/k/body";
    private const string KeyAndToken = "/k/key-and-token";
    private const string BrokenKey = "/k/broken";
    private const string EmptyKey = "/k/empty";

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
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton(_ => new SecureStore(_storePath)));
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

        _proxy.UseLocalAccessGuard();
        _proxy.MapReverseProxy();
        await _proxy.StartAsync();

        var cache = _proxy.Services.GetRequiredService<ConfigStoreCache>();

        var apiKey = new CredentialRecord
        {
            Name = "static-key",
            Kind = CredentialKind.ApiKey,
            ApiKey = StaticKey,
            DefaultPlacement = CredentialPlacement.Header,
            DefaultParameterName = "X-Api-Key",
            DefaultValuePrefix = "",
        };

        var oauth = new CredentialRecord
        {
            Name = "oauth",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(OAuthToken, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        // A key that picked up a line break on its way through a wrapped email. Stored as-is on
        // purpose: the editor rejects this, but a store written by hand or by an older build can
        // hold it, and it must never reach the wire.
        var brokenKey = new CredentialRecord
        {
            Name = "broken-key",
            Kind = CredentialKind.ApiKey,
            ApiKey = "KEY\r\nX-Admin: 1",
        };

        var emptyKey = new CredentialRecord { Name = "empty-key", Kind = CredentialKind.ApiKey, ApiKey = "" };

        var upstreamRecord = new UpstreamRecord { Name = "echo", BaseUrl = upstreamUrl };

        await cache.MutateAsync(store =>
        {
            store.Settings.LocalApiKey = ProxyKey;
            store.Credentials.Add(apiKey);
            store.Credentials.Add(oauth);
            store.Credentials.Add(brokenKey);
            store.Credentials.Add(emptyKey);
            store.Upstreams.Add(upstreamRecord);

            void AddRoute(string prefix, params RouteCredential[] credentials) =>
                store.Routes.Add(new RouteMapping
                {
                    PathPrefix = prefix,
                    UpstreamId = upstreamRecord.Id,
                    StripPrefix = true,
                    Credentials = [.. credentials],
                });

            AddRoute(KeyInHeader, Entry(apiKey.Id, CredentialPlacement.Header, "X-Api-Key", ""));
            AddRoute(KeyInQuery, Entry(apiKey.Id, CredentialPlacement.Query, "api_key", ""));
            AddRoute(KeyInBody, Entry(apiKey.Id, CredentialPlacement.Body, "api_key", ""));

            // The combination this whole feature exists for: an OAuth grant for the user's
            // identity plus a static project key the same API also demands.
            AddRoute(KeyAndToken,
                Entry(oauth.Id, CredentialPlacement.Header, "Authorization", "Bearer "),
                Entry(apiKey.Id, CredentialPlacement.Header, "X-Api-Key", ""));

            AddRoute(BrokenKey,
                Entry(oauth.Id, CredentialPlacement.Header, "Authorization", "Bearer "),
                Entry(brokenKey.Id, CredentialPlacement.Header, "X-Key", ""));

            AddRoute(EmptyKey, Entry(emptyKey.Id, CredentialPlacement.Header, "X-Api-Key", ""));
        });
        _proxy.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

        var proxyUrl = _proxy.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        _client = new HttpClient { BaseAddress = new Uri(proxyUrl) };
    }

    private static RouteCredential Entry(Guid id, CredentialPlacement placement, string name, string prefix) =>
        new() { CredentialId = id, Placement = placement, ParameterName = name, ValuePrefix = prefix };

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
    public async Task AStaticKeyIsForwardedInItsHeader()
    {
        var seen = await GetAsync($"{KeyInHeader}/resource");

        Assert.Equal(StaticKey, seen.Header("X-Api-Key"));
        Assert.Null(seen.Header("Authorization"));
    }

    [Fact]
    public async Task AStaticKeyIsForwardedInAQueryParameter()
    {
        var seen = await GetAsync($"{KeyInQuery}/resource?page=2");

        Assert.Contains($"api_key={StaticKey}", seen.Query);
        Assert.Contains("page=2", seen.Query);
    }

    [Fact]
    public async Task AStaticKeyIsForwardedInABodyField()
    {
        var seen = await PostJsonAsync($"{KeyInBody}/rpc", """{"method":"ping"}""");

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(StaticKey, json.RootElement.GetProperty("api_key").GetString());
        Assert.Equal("ping", json.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task AStaticKeyAndAnOAuthTokenCanRideTheSameRequest()
    {
        var seen = await GetAsync($"{KeyAndToken}/resource");

        Assert.Equal($"Bearer {OAuthToken}", seen.Header("Authorization"));
        Assert.Equal(StaticKey, seen.Header("X-Api-Key"));
    }

    [Fact]
    public async Task AKeyContainingALineBreakIsNeverPutOnTheWire()
    {
        // Request splitting, aimed at the upstream: the CR/LF would end the header line and the
        // rest would be read as a further header. Refused rather than sanitized — a silently
        // trimmed key is a key that does not work, reported as one that does.
        var seen = await GetAsync($"{BrokenKey}/resource");

        Assert.Null(seen.Header("X-Key"));
        Assert.Null(seen.Header("X-Admin"));

        // And the entry that was fine still went out: one bad credential costs only itself.
        Assert.Equal($"Bearer {OAuthToken}", seen.Header("Authorization"));
    }

    [Fact]
    public async Task ACredentialWithAnEmptyKeyForwardsUnauthenticatedRatherThanSendingABlankHeader()
    {
        // A blank header would be rejected by the upstream with a 401 that says nothing about
        // the real problem — that no key was ever stored.
        var seen = await GetAsync($"{EmptyKey}/resource");

        Assert.Null(seen.Header("X-Api-Key"));
    }

    [Fact]
    public async Task AStaticKeyReplacesACallerSuppliedHeaderOfTheSameName()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{KeyInHeader}/resource");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ProxyKey);
        request.Headers.Add("X-Api-Key", "CALLER-SUPPLIED");

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        Assert.Equal(StaticKey, Single().Header("X-Api-Key"));
    }

    [Fact]
    public async Task TheLocalApiKeyIsStillRequiredForAnApiKeyRoute()
    {
        // The proxy's own gate is unrelated to which kind of credential a route carries.
        var response = await _client.GetAsync($"{KeyInHeader}/resource");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_received);
    }

    private async Task<Seen> GetAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ProxyKey);

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        return Single();
    }

    private async Task<Seen> PostJsonAsync(string url, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ProxyKey);

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

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
