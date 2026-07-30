using Microsoft.Extensions.Logging.Abstractions;
using OAuthProxy.Core.Auth;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests.Vault;

/// <summary>
/// What the app does while the password manager is locked: no edits, and no token refresh on
/// either path.
///
/// Refusing to refresh is the load-bearing part, and it is not a limitation — it is the fix. A
/// refresh rotates the refresh token at the provider, and one that succeeds while the vault cannot
/// record the result leaves the replacement in memory only, so the next restart loses the grant
/// for good and the user has to reconnect having done nothing wrong. Not refreshing costs an
/// access token that expires until the vault reopens, which is temporary and self-explaining.
/// </summary>
public class ReadOnlyModeTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"oauthproxy-ro-{Guid.NewGuid()}");

    // ---- Edits ----------------------------------------------------------------------------------

    [Fact]
    public async Task MutateAsync_IsRefusedAndLeavesTheStoreUntouched()
    {
        var cache = await LockedCacheAsync();
        var before = cache.Current.Credentials.Count;

        await Assert.ThrowsAsync<VaultLockedException>(() => cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "nope", ClientId = "id", ClientSecret = "s" })));

        // Refused before the mutation runs rather than applied and rolled back, so there is no
        // window in which another thread could observe the half-applied edit.
        Assert.Equal(before, cache.Current.Credentials.Count);
    }

    [Fact]
    public async Task SaveAsync_IsRefused()
    {
        var cache = await LockedCacheAsync();

        await Assert.ThrowsAsync<VaultLockedException>(() => cache.SaveAsync());
    }

    [Fact]
    public async Task TheRefusalSaysWhatToDoAboutIt()
    {
        var cache = await LockedCacheAsync();

        var exception = await Assert.ThrowsAsync<VaultLockedException>(() => cache.SaveAsync());

        Assert.Contains("Unlock it", exception.Message);
    }

    [Fact]
    public async Task GoingBackToWritableRestoresEditing()
    {
        var cache = await LockedCacheAsync();
        cache.SetAccess(VaultAccess.Writable);

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "now-ok", ClientId = "id", ClientSecret = "s" }));

        Assert.Contains(cache.Current.Credentials, c => c.Name == "now-ok");
    }

    [Fact]
    public async Task AccessChangedFiresOncePerTransition()
    {
        // The UI greys out its buttons on this event and the refresh loop logs on it. Firing on
        // every no-op set would bury the activity log under one line a minute.
        var cache = new ConfigStoreCache(InMemoryVault.Empty());
        await cache.InitializeAsync();

        var transitions = new List<VaultAccess>();
        cache.AccessChanged += transitions.Add;

        cache.SetAccess(VaultAccess.ReadOnly);
        cache.SetAccess(VaultAccess.ReadOnly);
        cache.SetAccess(VaultAccess.Writable);

        Assert.Equal([VaultAccess.ReadOnly, VaultAccess.Writable], transitions);
    }

    // ---- The periodic refresh loop ---------------------------------------------------------------

    [Fact]
    public async Task TheRefreshLoopAttemptsNothingAndSaysWhyExactlyOnce()
    {
        var (cache, refresher, activityLog) = await LockedRefresherAsync(TimeSpan.FromMinutes(1));

        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);
        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);
        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);

        var lines = activityLog.GetRecent(100);

        // No attempt against the provider: the credential is well inside the 10-minute window and
        // would otherwise have been refreshed on the first pass.
        Assert.DoesNotContain(lines, line => line.Contains("expiring soon"));

        // Said once, not once a minute.
        Assert.Single(lines, line => line.Contains("REFRESH paused"));
        Assert.Contains(lines, line => line.Contains("the first token expires"));
    }

    [Fact]
    public async Task TheRefreshLoopResumesWhenTheVaultComesBack()
    {
        var (cache, refresher, activityLog) = await LockedRefresherAsync(TimeSpan.FromMinutes(1));

        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);
        cache.SetAccess(VaultAccess.Writable);
        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);

        // It tries immediately rather than sitting out a backoff. Recording a failure for a
        // credential that was never attempted would have delayed this by up to an hour — for
        // something that was never that credential's fault.
        Assert.Contains(activityLog.GetRecent(100), line => line.Contains("expiring soon"));
    }

    // ---- The on-demand path, inside the request pipeline ------------------------------------------

    [Fact]
    public async Task AnExpiredTokenIsReportedUnusableRatherThanRefreshed()
    {
        var (cache, provider, activityLog) = await LockedAccessTokenProviderAsync(TimeSpan.FromSeconds(-60));

        var token = await provider.GetAccessTokenAsync(cache.Current.Credentials[0].Id);

        // Null, not the stale token: the transform turns null into a named failure, while a stale
        // token would go to the upstream and come back as an opaque 401.
        Assert.Null(token);
        Assert.Contains(activityLog.GetRecent(100), line => line.Contains("not attempted"));
    }

    [Fact]
    public async Task AStillValidTokenIsReturnedNormally()
    {
        // A lock must not break routes that are working. Only a token that has actually expired
        // is affected, which is what makes the outage gradual rather than immediate.
        var (cache, provider, _) = await LockedAccessTokenProviderAsync(TimeSpan.FromHours(1));

        Assert.Equal("ACCESS", await provider.GetAccessTokenAsync(cache.Current.Credentials[0].Id));
    }

    [Fact]
    public async Task AnApiKeyIsReturnedRegardlessOfTheLock()
    {
        // API-key credentials never refresh, so a lock has no bearing on them and their routes
        // keep working throughout.
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord { Name = "key", Kind = CredentialKind.ApiKey, ApiKey = "STATIC" });

        var cache = new ConfigStoreCache(InMemoryVault.Empty().Seeded(store));
        await cache.InitializeAsync();
        cache.SetAccess(VaultAccess.ReadOnly);

        var provider = new AccessTokenProvider(cache, NewOAuth2Service(), NewLog());

        Assert.Equal("STATIC", await provider.GetAccessTokenAsync(cache.Current.Credentials[0].Id));
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private async Task<ConfigStoreCache> LockedCacheAsync()
    {
        var cache = new ConfigStoreCache(InMemoryVault.Empty());
        await cache.InitializeAsync();
        cache.SetAccess(VaultAccess.ReadOnly);
        return cache;
    }

    private async Task<(ConfigStoreCache, TokenRefreshService, ActivityLog)> LockedRefresherAsync(TimeSpan expiresIn)
    {
        var cache = await SeededLockedCacheAsync(expiresIn);
        var activityLog = NewLog();

        var refresher = new TokenRefreshService(
            cache, NewOAuth2Service(), activityLog, NullLogger<TokenRefreshService>.Instance);

        return (cache, refresher, activityLog);
    }

    private async Task<(ConfigStoreCache, AccessTokenProvider, ActivityLog)> LockedAccessTokenProviderAsync(
        TimeSpan expiresIn)
    {
        var cache = await SeededLockedCacheAsync(expiresIn);
        var activityLog = NewLog();

        return (cache, new AccessTokenProvider(cache, NewOAuth2Service(), activityLog), activityLog);
    }

    private static async Task<ConfigStoreCache> SeededLockedCacheAsync(TimeSpan expiresIn)
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "oauth",
            ClientId = "id",
            ClientSecret = "secret",
            // Deliberately unreachable. Nothing here may ever get far enough to call it — a test
            // that started making real network requests would be proving the opposite of the point.
            TokenEndpoint = "https://token-endpoint.invalid/token",
            Token = new TokenSet("ACCESS", "REFRESH", DateTimeOffset.UtcNow + expiresIn, "Bearer", DateTimeOffset.UtcNow),
        });

        var cache = new ConfigStoreCache(InMemoryVault.Empty().Seeded(store));
        await cache.InitializeAsync();
        cache.SetAccess(VaultAccess.ReadOnly);

        return cache;
    }

    private OAuth2Service NewOAuth2Service() => new(new GoogleOAuthService(NewLog()), NewLog());

    private ActivityLog NewLog() => new(_logPath);

    public void Dispose()
    {
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }
}
