using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Tests;

/// <summary>
/// The store is the only copy of the config — there is no local cache to fall back on — so the
/// behaviour that matters most is what happens when a save fails. These pin it.
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

        // And they must be persisted here rather than only held in memory, or the next launch
        // would issue different ones.
        var reloaded = await vault.LoadAsync();
        Assert.Equal(routeKey.Value, reloaded.Routes[0].Key.Value);
        Assert.Equal(funnelKey.Value, reloaded.McpFunnels[0].Key.Value);
    }

    [Fact]
    public async Task Initialize_KeepsTheSameEndpointKeysAcrossRestarts()
    {
        // The failure this guards against: a generated-by-default key looks "already set" to
        // the backfill, so it never reaches the vault and the next launch invents a different
        // one — every configured client starts getting 403 at random.
        var seed = new ConfigStore();
        seed.Routes.Add(new RouteMapping { PathPrefix = "/app/one" });

        // One vault across all the "restarts", which is what a restart actually looks like: the
        // process is new, the vault is not.
        var vault = InMemoryVault.Empty().Seeded(seed);

        var firstRun = new ConfigStoreCache(vault);
        await firstRun.InitializeAsync();
        var originalKey = firstRun.Current.Routes[0].Key.Value;

        Assert.False(string.IsNullOrEmpty(originalKey));

        for (var restart = 0; restart < 3; restart++)
        {
            var laterRun = new ConfigStoreCache(vault);
            await laterRun.InitializeAsync();
            Assert.Equal(originalKey, laterRun.Current.Routes[0].Key.Value);
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

        var savers = Enumerable.Range(0, 25).Select(_ => Task.Run(() => cache.SaveAsync()));

        await Task.WhenAll(writers.Concat(savers));

        Assert.Equal(25, cache.Current.Credentials.Count);
        var reloaded = await vault.LoadAsync();
        Assert.Equal(25, reloaded.Credentials.Count);
    }

    [Fact]
    public async Task MutateAsync_WhenTheSaveFailsBeforeWriting_UndoesTheMutation()
    {
        // The original failure: a failed write left the edit applied in memory. The UI showed the
        // new credential and the proxy routed to it, then it vanished at the next restart —
        // silent data loss discovered hours later.
        var cache = new ConfigStoreCache(InMemoryVault.ThatFailsBeforeWriting());

        var existing = new CredentialRecord { Name = "keep", ClientId = "id", ClientSecret = "secret" };
        cache.Current.Credentials.Add(existing);

        await Assert.ThrowsAnyAsync<Exception>(() => cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "doomed", ClientId = "id", ClientSecret = "secret" })));

        var survivor = Assert.Single(cache.Current.Credentials);
        Assert.Equal("keep", survivor.Name);

        // Restored in place rather than by swapping in a clone: the view models hold direct
        // references to these records, so a fresh object graph would leave every binding
        // pointing at an orphan.
        Assert.Same(existing, survivor);

        Assert.False(cache.IsOutOfSync);
    }

    [Fact]
    public async Task MutateAsync_WhenTheSaveFailsPartWayThrough_KeepsTheMutationAndReportsOutOfSync()
    {
        // The mirror image of the test above, and the reason the two cases cannot share a code
        // path. Saving the store is several vault items; when some of them are already durable,
        // reverting memory would make the next successful save delete records that are safely
        // stored. Memory stays ahead, and the user is told rather than silently corrected.
        var cache = new ConfigStoreCache(InMemoryVault.ThatFailsHalfway());

        await Assert.ThrowsAsync<VaultSaveException>(() => cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "half-saved", ClientId = "id", ClientSecret = "secret" })));

        var kept = Assert.Single(cache.Current.Credentials);
        Assert.Equal("half-saved", kept.Name);
        Assert.True(cache.IsOutOfSync);
    }

    [Fact]
    public async Task MutateAsync_WhenTheSaveFails_UndoesSettingsChangesToo()
    {
        var cache = new ConfigStoreCache(InMemoryVault.ThatFailsBeforeWriting());
        var originalPort = cache.Current.Settings.ListenPort;

        await Assert.ThrowsAnyAsync<Exception>(() => cache.MutateAsync(store =>
        {
            store.Settings.ListenPort = 9999;
            store.Settings.McpFunnelEnabled = true;
        }));

        Assert.Equal(originalPort, cache.Current.Settings.ListenPort);
        Assert.False(cache.Current.Settings.McpFunnelEnabled);
    }

    [Fact]
    public async Task MutateAsync_WhenTheSaveFails_UndoesMcpFunnelChangesToo()
    {
        // The rollback snapshot names every list explicitly, so a list added to ConfigStore
        // without being added there is silently exempt from rollback — exactly the silent
        // data-loss bug this whole mechanism exists to prevent, reintroduced quietly.
        var cache = new ConfigStoreCache(InMemoryVault.ThatFailsBeforeWriting());

        await Assert.ThrowsAnyAsync<Exception>(() => cache.MutateAsync(store =>
        {
            store.McpSources.Add(new McpSourceRecord { Name = "doomed", Alias = "d", Url = "https://example.com/mcp" });
            store.McpFunnels.Add(new McpFunnelRecord { Name = "doomed", Slug = "doomed" });
            store.Settings.McpFunnelEnabled = true;
        }));

        Assert.Empty(cache.Current.McpSources);
        Assert.Empty(cache.Current.McpFunnels);
        Assert.False(cache.Current.Settings.McpFunnelEnabled);
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
    public async Task MutateAsync_WhenTheSaveSucceeds_KeepsTheMutation()
    {
        var vault = InMemoryVault.Empty();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "kept", ClientId = "id", ClientSecret = "secret" }));

        Assert.Single(cache.Current.Credentials);
        Assert.Single((await vault.LoadAsync()).Credentials);
    }

    [Fact]
    public async Task ReloadAsync_DiscardsInMemoryStateAndClearsOutOfSync()
    {
        // The escape hatch from a half-applied save, and from a secret edited in the password
        // manager's own UI — neither of which the app can resolve on its own.
        var seed = new ConfigStore();
        seed.Credentials.Add(new CredentialRecord { Name = "in-vault", ClientId = "id", ClientSecret = "secret" });

        var cache = new ConfigStoreCache(InMemoryVault.Empty().Seeded(seed));
        await cache.InitializeAsync();

        cache.Current.Credentials.Add(new CredentialRecord { Name = "only-in-memory", ClientId = "id", ClientSecret = "s" });

        await cache.ReloadAsync();

        var survivor = Assert.Single(cache.Current.Credentials);
        Assert.Equal("in-vault", survivor.Name);
        Assert.False(cache.IsOutOfSync);
    }
}
