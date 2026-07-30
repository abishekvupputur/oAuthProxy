using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;
using Yarp.ReverseProxy.Configuration;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// Two things used to happen at startup with nothing said about either: an unreadable store
/// took every credential, route, and upstream with it (and issued a new API key, so every
/// configured client started getting 403), and a plain-http endpoint stored by an older build
/// kept putting tokens and client secrets on the wire in cleartext.
/// </summary>
public class StartupDiagnosticsTests : IDisposable
{
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"oauthproxy-test-{Guid.NewGuid()}.dat");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"oauthproxy-test-logs-{Guid.NewGuid()}");

    [Fact]
    public async Task Startup_AfterAStoreIsQuarantined_SaysSoInTheActivityLog()
    {
        // Not DPAPI-protected, so Unprotect throws - the shape of a profile copied from another
        // machine, or a file truncated by a hard power loss.
        await File.WriteAllBytesAsync(_storePath, [0x00, 0x01, 0x02, 0x03, 0x04]);

        var (activityLog, secureStore) = await RunStartupAsync();

        Assert.NotNull(secureStore.QuarantinedFilePath);

        var lines = activityLog.GetRecent(100);
        Assert.Contains(lines, line => line.Contains("could not be read and was renamed"));
        Assert.Contains(lines, line => line.Contains("new local API key has been generated"));
    }

    [Fact]
    public async Task Startup_WithACleanStore_SaysNothingAboutQuarantine()
    {
        var (activityLog, secureStore) = await RunStartupAsync();

        Assert.Null(secureStore.QuarantinedFilePath);
        Assert.DoesNotContain(activityLog.GetRecent(100), line => line.Contains("renamed"));
    }

    [Fact]
    public async Task Startup_WithAPlainHttpUpstream_WarnsThatTheTokenWouldTravelInCleartext()
    {
        // Validation only ever ran when a record was added, so anything stored by an older
        // build was never re-checked.
        var store = new ConfigStore();
        store.Upstreams.Add(new UpstreamRecord { Name = "legacy", BaseUrl = "http://api.example.com" });
        await new SecureStore(_storePath).SaveAsync(store);

        var (activityLog, _) = await RunStartupAsync();

        Assert.Contains(activityLog.GetRecent(100), line =>
            line.Contains("STARTUP WARNING") && line.Contains("legacy") && line.Contains("cleartext"));
    }

    [Fact]
    public async Task Startup_WithAPlainHttpTokenEndpoint_WarnsAboutTheCredential()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "legacy-credential",
            ClientId = "id",
            ClientSecret = "secret",
            TokenEndpoint = "http://idp.example.com/token",
        });
        await new SecureStore(_storePath).SaveAsync(store);

        var (activityLog, _) = await RunStartupAsync();

        Assert.Contains(activityLog.GetRecent(100), line =>
            line.Contains("STARTUP WARNING") && line.Contains("legacy-credential"));
    }

    [Fact]
    public async Task Startup_WithHttpsEndpointsAndLoopbackUpstreams_WarnsAboutNothing()
    {
        var store = new ConfigStore();
        store.Upstreams.Add(new UpstreamRecord { Name = "secure", BaseUrl = "https://api.example.com" });

        // Plain http is fine for a local development upstream - it never leaves the machine.
        store.Upstreams.Add(new UpstreamRecord { Name = "local-dev", BaseUrl = "http://127.0.0.1:8080" });
        await new SecureStore(_storePath).SaveAsync(store);

        var (activityLog, _) = await RunStartupAsync();

        Assert.DoesNotContain(activityLog.GetRecent(100), line => line.Contains("STARTUP WARNING"));
    }

    private async Task<(ActivityLog ActivityLog, SecureStore SecureStore)> RunStartupAsync()
    {
        var activityLog = new ActivityLog(_logPath);
        var secureStore = new SecureStore(_storePath);
        var cache = new ConfigStoreCache(secureStore);
        var notifier = new ProxyConfigChangeNotifier(cache, new InMemoryConfigProvider([], []), activityLog);

        await new ConfigStoreInitializerHostedService(cache, secureStore, notifier, activityLog)
            .StartAsync(CancellationToken.None);

        return (activityLog, secureStore);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(
                     Path.GetDirectoryName(_storePath)!,
                     Path.GetFileName(_storePath) + "*"))
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }

        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }
}

