using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// The in-memory store: what it accepts, and what it considers still owing to the vault.
///
/// Note what is deliberately absent — there are no rollback tests any more. A mutation no longer
/// waits for a write, so there is no failed write to undo at the point of the edit; the change is
/// simply pending until the sync queue lands it. <see cref="Vault.DeferredSyncTests"/> covers that.
/// </summary>
public class ConfigStoreResilienceTests
{
    [Fact]
    public async Task Initialize_IssuesAKeyToEveryRouteAndFunnelThatHasNone()
    {
        // A route or funnel can reach the vault without a key: created by hand in the password
        // manager, or restored from a vault item whose key item is gone. Without the backfill the
        // access guard has nothing to compare against and every request to it is refused.
        var seed = new ConfigStore();
        seed.Routes.Add(new RouteMapping { PathPrefix = "/app/keyless" });
        seed.McpFunnels.Add(new McpFunnelRecord { Name = "keyless", Slug = "keyless" });

        var vault = InMemoryVault.Empty().Seeded(seed);

        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var routeKey = cache.Current.Routes[0].Key;
        var funnelKey = cache.Current.McpFunnels[0].Key;

        Assert.True(routeKey.IsConfigured);
        Assert.True(funnelKey.IsConfigured);

        // Separately generated, or the whole point of per-endpoint keys is lost.
        Assert.NotEqual(routeKey.Value, funnelKey.Value);

        // Never-expiring, so a backfill cannot silently lock someone out on a timer.
        Assert.Null(routeKey.ExpiresUtc);

        // And they are owed to the vault immediately. Left un-pending they would only ever live in
        // memory, and the next launch would issue different ones — every configured client
        // breaking at random.
        Assert.True(cache.HasPendingChanges);
    }

    [Fact]
    public async Task Initialize_KeepsTheSameEndpointKeysAcrossRestarts()
    {
        // The failure this guards against: a generated-by-default key looks "already set" to the
        // backfill, so it never reaches the vault and the next launch invents a different one.
        var seed = new ConfigStore();
        seed.Routes.Add(new RouteMapping { PathPrefix = "/app/one" });

        // One vault across all the "restarts", which is what a restart actually looks like: the
        // process is new, the vault is not.
        var vault = InMemoryVault.Empty().Seeded(seed);

        var firstRun = new ConfigStoreCache(vault);
        await firstRun.InitializeAsync();
        await vault.SaveAsync(firstRun.Current);

        var originalKey = firstRun.Current.Routes[0].Key.Value;
        Assert.False(string.IsNullOrEmpty(originalKey));

        for (var restart = 0; restart < 3; restart++)
        {
            var laterRun = new ConfigStoreCache(vault);
            await laterRun.InitializeAsync();

            Assert.Equal(originalKey, laterRun.Current.Routes[0].Key.Value);

            // Nothing to backfill this time, so nothing is owed.
            Assert.False(laterRun.HasPendingChanges);
        }
    }

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        // Startup loads the store itself (it needs the listen port before Kestrel can bind) and
        // ConfigStoreInitializerHostedService loads it again as the host starts. The second call
        // must not re-read the vault, or any edit made in between is silently discarded.
        var vault = InMemoryVault.Empty();
        var cache = new ConfigStoreCache(vault);

        await cache.InitializeAsync();
        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "added", ClientId = "id", ClientSecret = "secret" }));

        await cache.InitializeAsync();

        Assert.Single(cache.Current.Credentials);
        Assert.True(cache.IsInitialized);
    }

    [Fact]
    public void ANewProxyKey_HasNoValueUntilItIsGenerated()
    {
        // Generation belongs to route/funnel creation and to InitializeAsync alone, so that it is
        // always paired with a save.
        Assert.Equal("", new ProxyKey().Value);
        Assert.False(new ProxyKey().IsConfigured);
        Assert.False(new RouteMapping { PathPrefix = "/app/x" }.Key.IsConfigured);
        Assert.False(new McpFunnelRecord { Name = "n", Slug = "n" }.Key.IsConfigured);
    }

    [Fact]
    public void GenerateApiKey_ProducesDistinctUrlSafeKeys()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ProxyKey.GenerateValue()).ToList();

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
        var vault = InMemoryVault.Empty();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var writers = Enumerable.Range(0, 25).Select(i => Task.Run(() =>
            cache.MutateAsync(store => store.Credentials.Add(new CredentialRecord
            {
                Name = $"cred-{i}",
                ClientId = "id",
                ClientSecret = "secret",
            }))));

        // Snapshots taken concurrently must never observe a half-applied edit either — that is
        // exactly what the sync queue does on its own thread while the UI keeps writing.
        var readers = Enumerable.Range(0, 25).Select(_ => Task.Run(() => cache.SnapshotForSyncAsync()));

        await Task.WhenAll(writers.Concat<Task>(readers));

        Assert.Equal(25, cache.Current.Credentials.Count);
    }

    [Fact]
    public async Task ASnapshotIsDetachedFromTheLiveStore()
    {
        // The vault write runs outside the lock against this snapshot, so it has to be a real
        // copy. Sharing references would let an edit made mid-write be serialized halfway.
        var cache = new ConfigStoreCache(InMemoryVault.Empty());
        await cache.InitializeAsync();

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "original", ClientId = "id", ClientSecret = "s" }));

        var (snapshot, _) = await cache.SnapshotForSyncAsync();

        await cache.MutateAsync(store => store.Credentials[0].Name = "renamed");

        Assert.Equal("original", snapshot.Credentials[0].Name);
        Assert.Equal("renamed", cache.Current.Credentials[0].Name);
    }

    [Fact]
    public async Task McpFunnelConfiguration_SurvivesARoundTripToTheVault()
    {
        var vault = InMemoryVault.Empty();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var source = new McpSourceRecord
        {
            Name = "github",
            Alias = "gh",
            Kind = McpSourceKind.RemoteUrl,
            Url = "https://example.com/mcp",
            Transport = McpTransportPreference.Sse,
        };

        await cache.MutateAsync(store =>
        {
            store.Settings.McpFunnelEnabled = true;
            store.McpSources.Add(source);
            store.McpFunnels.Add(new McpFunnelRecord
            {
                Name = "coding agent",
                Slug = "coding-agent",
                Sources =
                [
                    new McpFunnelSource
                    {
                        SourceId = source.Id,
                        ToolMode = McpSelectionMode.Include,
                        Tools = ["create_issue"],
                    },
                ],
            });
        });

        var (snapshot, _) = await cache.SnapshotForSyncAsync();
        await vault.SaveAsync(snapshot);

        var reloaded = await vault.LoadAsync();

        Assert.True(reloaded.Settings.McpFunnelEnabled);

        var reloadedSource = Assert.Single(reloaded.McpSources);
        Assert.Equal("gh", reloadedSource.Alias);
        Assert.Equal(McpTransportPreference.Sse, reloadedSource.Transport);

        var reloadedFunnel = Assert.Single(reloaded.McpFunnels);
        var link = Assert.Single(reloadedFunnel.Sources);
        Assert.Equal(source.Id, link.SourceId);
        Assert.Equal(McpSelectionMode.Include, link.ToolMode);
        Assert.Equal(["create_issue"], link.Tools);
    }

    [Fact]
    public async Task ReloadAsync_DiscardsInMemoryStateAndClearsWhatWasOwed()
    {
        // The escape hatch for a secret edited in the password manager's own UI, which nothing
        // here can be notified about.
        var seed = new ConfigStore();
        seed.Credentials.Add(new CredentialRecord { Name = "in-vault", ClientId = "id", ClientSecret = "secret" });

        var cache = new ConfigStoreCache(InMemoryVault.Empty().Seeded(seed));
        await cache.InitializeAsync();

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "only-in-memory", ClientId = "id", ClientSecret = "s" }));

        await cache.ReloadAsync();

        var survivor = Assert.Single(cache.Current.Credentials);
        Assert.Equal("in-vault", survivor.Name);

        // The pending change was thrown away deliberately, so nothing is still owed.
        Assert.False(cache.HasPendingChanges);
    }
}
