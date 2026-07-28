using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// Startup used to die on an unreadable store, and the dispatcher's catch-all then left a live
/// process with no window and no tray icon. These pin the recovery behavior that replaced it.
/// </summary>
public class ConfigStoreResilienceTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"oauthproxy-test-{Guid.NewGuid()}.dat");

    [Fact]
    public async Task Load_UnreadableFile_QuarantinesItAndReturnsEmptyStore()
    {
        // Not DPAPI-protected, so Unprotect throws — same shape as a store copied from another
        // machine or truncated by a power loss.
        await File.WriteAllBytesAsync(_tempFile, [0x00, 0x01, 0x02, 0x03, 0x04]);

        var secureStore = new SecureStore(_tempFile);
        var loaded = await secureStore.LoadAsync();

        Assert.Empty(loaded.Credentials);
        Assert.NotNull(secureStore.QuarantinedFilePath);
        Assert.True(File.Exists(secureStore.QuarantinedFilePath),
            "the unreadable file should be kept aside, not deleted");
        Assert.False(File.Exists(_tempFile));
    }

    [Fact]
    public async Task Initialize_BackfillsMissingApiKey()
    {
        // A store written before the local API key existed. Without a backfill the guard has
        // nothing to compare against and every request would be refused.
        var store = new ConfigStore();
        store.Settings.LocalApiKey = "";
        var secureStore = new SecureStore(_tempFile);
        await secureStore.SaveAsync(store);

        var cache = new ConfigStoreCache(secureStore);
        await cache.InitializeAsync();

        Assert.False(string.IsNullOrEmpty(cache.Current.Settings.LocalApiKey));

        // And it must survive a restart rather than being regenerated every launch.
        var reloaded = await new SecureStore(_tempFile).LoadAsync();
        Assert.Equal(cache.Current.Settings.LocalApiKey, reloaded.Settings.LocalApiKey);
    }

    [Fact]
    public async Task Initialize_KeepsTheSameApiKeyAcrossRestarts()
    {
        // The failure this guards against: a generated-by-default key looks "already set" to
        // the backfill, so it never reaches disk and the next launch invents a different one —
        // every configured client starts getting 403 at random.
        var firstRun = new ConfigStoreCache(new SecureStore(_tempFile));
        await firstRun.InitializeAsync();
        var originalKey = firstRun.Current.Settings.LocalApiKey;

        Assert.False(string.IsNullOrEmpty(originalKey));

        for (var restart = 0; restart < 3; restart++)
        {
            var laterRun = new ConfigStoreCache(new SecureStore(_tempFile));
            await laterRun.InitializeAsync();
            Assert.Equal(originalKey, laterRun.Current.Settings.LocalApiKey);
        }
    }

    [Fact]
    public void NewAppSettings_HasNoApiKeyUntilInitialized()
    {
        // Generation belongs to InitializeAsync alone, so that it is always paired with a save.
        Assert.Equal("", new AppSettings().LocalApiKey);
    }

    [Fact]
    public void GenerateApiKey_ProducesDistinctUrlSafeKeys()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => AppSettings.GenerateApiKey()).ToList();

        Assert.Equal(100, keys.Distinct().Count());
        Assert.All(keys, key =>
        {
            Assert.True(key.Length >= 40, "32 random bytes should survive base64 at >= 40 chars");
            // Safe to put in a query string without escaping.
            Assert.DoesNotContain('+', key);
            Assert.DoesNotContain('/', key);
            Assert.DoesNotContain('=', key);
        });
    }

    [Fact]
    public async Task MutateAsync_SerializesConcurrentWritersWithoutThrowing()
    {
        // The original failure: the UI thread adding a credential while the refresh loop was
        // serializing the same store threw "Collection was modified" out of the refresh loop,
        // which stopped the host and silently killed the proxy.
        var cache = new ConfigStoreCache(new SecureStore(_tempFile));
        await cache.InitializeAsync();

        var writers = Enumerable.Range(0, 25).Select(i => Task.Run(() =>
            cache.MutateAsync(store => store.Credentials.Add(new CredentialRecord
            {
                Name = $"cred-{i}",
                ClientId = "id",
                ClientSecret = "secret",
            }))));

        var savers = Enumerable.Range(0, 25).Select(_ => Task.Run(() => cache.SaveAsync()));

        await Task.WhenAll(writers.Concat(savers));

        Assert.Equal(25, cache.Current.Credentials.Count);
        var reloaded = await new SecureStore(_tempFile).LoadAsync();
        Assert.Equal(25, reloaded.Credentials.Count);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(
                     Path.GetDirectoryName(_tempFile)!,
                     Path.GetFileName(_tempFile) + "*"))
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }
}
