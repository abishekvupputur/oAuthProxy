using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// Nothing from one vault may appear under another.
///
/// The store is a singleton and the vault behind it is <c>GatedConfigVault</c>, which forwards to
/// whichever backend the gate has settled on — so the backend can change underneath the cache
/// without the cache being told. It used to guard its load with a plain "have I loaded anything"
/// flag, which answers the wrong question: after picking a different password manager, or a
/// different vault in the same one, that flag was still true, so the load was skipped and the app
/// carried on serving the previous vault's configuration. The next save then wrote it into the
/// newly chosen vault.
///
/// These pin the load being keyed to <em>where the store came from</em>, and the disconnect leaving
/// nothing behind.
/// </summary>
public class VaultSwitchIsolationTests
{
    [Fact]
    public async Task ChoosingAnotherVault_ReloadsInsteadOfKeepingTheOldConfiguration()
    {
        var vault = new SwitchableVault();
        vault.Contents["Personal"] = StoreWith("from-personal");
        vault.Contents["Work"] = StoreWith("from-work");

        var cache = new ConfigStoreCache(vault);

        vault.VaultName = "Personal";
        await cache.InitializeAsync();
        Assert.Equal("from-personal", cache.Current.Credentials.Single().Name);

        // The user picks the other vault. Same backend, same cache, same everything except the
        // vault -- which is exactly the case the old flag missed.
        vault.VaultName = "Work";
        await cache.InitializeAsync();

        Assert.Equal("from-work", cache.Current.Credentials.Single().Name);
    }

    [Fact]
    public async Task ChoosingAnotherBackend_ReloadsToo()
    {
        var vault = new SwitchableVault();
        vault.Contents["RavensPort"] = StoreWith("from-1password");

        var cache = new ConfigStoreCache(vault);

        vault.Kind = VaultBackendKind.OnePassword;
        await cache.InitializeAsync();
        Assert.Equal("from-1password", cache.Current.Credentials.Single().Name);

        vault.Kind = VaultBackendKind.ProtonPass;
        vault.Contents["RavensPort"] = StoreWith("from-proton");
        await cache.InitializeAsync();

        Assert.Equal("from-proton", cache.Current.Credentials.Single().Name);
    }

    [Fact]
    public async Task LoadingTheSameVaultTwice_DoesNotReRead()
    {
        // The idempotence the original flag existed for still has to hold: startup calls this
        // directly and the hosted service calls it again, and a second read would discard edits
        // made in between.
        var vault = new SwitchableVault();
        vault.Contents["RavensPort"] = StoreWith("only");

        var cache = new ConfigStoreCache(vault);

        await cache.InitializeAsync();
        await cache.InitializeAsync();

        Assert.Equal(1, vault.Loads);
    }

    [Fact]
    public async Task AStoreFromAnotherVault_IsReportedRatherThanSaved()
    {
        // What the sync queue checks before writing. Between the gate repointing and the reload
        // landing, memory holds the previous vault's configuration -- and writing it would copy one
        // vault's credentials into another, then prune the destination to match.
        var vault = new SwitchableVault();
        vault.Contents["Personal"] = StoreWith("from-personal");

        var cache = new ConfigStoreCache(vault);

        vault.VaultName = "Personal";
        await cache.InitializeAsync();
        Assert.False(cache.IsFromAnotherVault);

        vault.VaultName = "Work";
        Assert.True(cache.IsFromAnotherVault);
    }

    [Fact]
    public async Task Resetting_LeavesNothingOfTheVaultBehind()
    {
        // Disconnect. The store is emptied, and the record of where it came from goes with it --
        // otherwise a later load of that same vault would be skipped as already done, against a
        // store that is now empty.
        var vault = new SwitchableVault();
        vault.Contents["RavensPort"] = StoreWith("something");

        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        await cache.ResetAsync();

        Assert.Empty(cache.Current.Credentials);
        Assert.False(cache.IsInitialized);
        Assert.False(cache.IsFromAnotherVault);
        Assert.Null(cache.LastLoadNotice);

        // And a reconnect to the very same vault reads it again rather than short-circuiting.
        await cache.InitializeAsync();

        Assert.Equal("something", cache.Current.Credentials.Single().Name);
        Assert.Equal(2, vault.Loads);
    }

    [Fact]
    public async Task TheListenPortSurvivesAReset_BecauseKestrelIsAlreadyBoundToIt()
    {
        // The one deliberate exception, kept explicit so it is not "fixed" later: the Settings tab
        // would otherwise show a port that is not the one in use.
        var vault = new SwitchableVault();
        vault.Contents["RavensPort"] = StoreWith("something");

        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        cache.Current.Settings.ListenPort = 5599;
        await cache.ResetAsync();

        Assert.Equal(5599, cache.Current.Settings.ListenPort);
    }

    private static ConfigStore StoreWith(string credentialName) =>
        new()
        {
            Credentials =
            {
                new CredentialRecord { Name = credentialName, Kind = CredentialKind.ApiKey, ApiKey = "k" },
            },
        };

    /// <summary>
    /// Stands in for GatedConfigVault: one object whose backend and vault can change underneath the
    /// cache, which is the whole reason the cache cannot trust a bare "loaded" flag.
    /// </summary>
    private sealed class SwitchableVault : IConfigVault
    {
        private readonly InMemoryVault _inner = new();

        public VaultBackendKind Kind { get; set; } = VaultBackendKind.OnePassword;
        public string VaultName { get; set; } = "RavensPort";

        public Dictionary<string, ConfigStore> Contents { get; } = [];
        public int Loads { get; private set; }

        public string? LastLoadWarning => null;
        public IReadOnlyList<string> LastLoadRemovals => [];

        public Task<ConfigStore> LoadAsync(CancellationToken ct = default)
        {
            Loads++;

            // A fresh graph each time, as a real provider returns -- so a test cannot pass by
            // accidentally sharing the same object between two "vaults".
            var source = Contents.TryGetValue(VaultName, out var store) ? store : new ConfigStore();

            return Task.FromResult(new ConfigStore
            {
                Credentials = [.. source.Credentials.Select(c => new CredentialRecord
                {
                    Id = c.Id, Name = c.Name, Kind = c.Kind, ApiKey = c.ApiKey,
                })],
            });
        }

        public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) => _inner.ProbeAsync(ct);

        public Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default) =>
            _inner.ProbeAsync(depth, ct);
        public Task CreateVaultAsync(string vaultName, CancellationToken ct = default) => _inner.CreateVaultAsync(vaultName, ct);
        public Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default) => _inner.UseExistingVaultAsync(vaultName, ct);
        public void Forget() => _inner.Forget();
        public Task SaveAsync(ConfigStore store, CancellationToken ct = default) => Task.CompletedTask;
        public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default) => _inner.ListLiveItemsAsync(ct);
        public Task DeleteItemAsync(string itemId, CancellationToken ct = default) => _inner.DeleteItemAsync(itemId, ct);
    }
}
