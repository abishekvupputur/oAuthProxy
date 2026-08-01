using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// When the vault-maintenance actions on the Settings tab are allowed to run.
///
/// Those actions — the integrity check and the deletes it offers, rewrite-all, re-initialise — all
/// compare what is in memory against what is in the vault and present the difference as items to
/// delete and records to drop. The comparison is only meaningful once the vault has actually been
/// read. Mid-load the store is empty or half-replaced, so it inverts: real items look orphaned,
/// real records look missing, and every button beside those lists removes something.
///
/// <see cref="ConfigStoreCache.IsSettled"/> is the gate. These pin its edges, especially the one
/// that matters most — that a load which throws still leaves the gate open, since a stuck flag
/// would disable vault maintenance with no way back.
/// </summary>
public class VaultLoadGateTests
{
    [Fact]
    public void IsSettled_IsFalse_BeforeAnythingHasBeenLoaded()
    {
        var cache = new ConfigStoreCache(new InMemoryVault());

        Assert.False(cache.IsInitialized);
        Assert.False(cache.IsSettled);
    }

    [Fact]
    public async Task IsSettled_IsTrue_OnceTheFirstLoadHasFinished()
    {
        var cache = new ConfigStoreCache(new InMemoryVault());

        await cache.InitializeAsync();

        Assert.True(cache.IsSettled);
        Assert.False(cache.IsLoading);
    }

    [Fact]
    public async Task IsLoading_IsTrue_WhileTheVaultIsBeingRead()
    {
        // The window the gate exists for. Held open by a vault that has not returned yet, which is
        // what a slow Proton Pass read looks like from here.
        var vault = new BlockingVault();
        var cache = new ConfigStoreCache(vault);

        var loading = cache.InitializeAsync();

        await vault.Entered.Task;

        Assert.True(cache.IsLoading);
        Assert.False(cache.IsSettled);

        vault.Release();
        await loading;

        Assert.True(cache.IsSettled);
    }

    [Fact]
    public async Task IsLoading_IsTrue_DuringAReloadOfAnAlreadyLoadedStore()
    {
        // Re-initialise, and the reconnect path. The tab is live and visible during this one, so it
        // is the case a user can actually reach with a mouse.
        var vault = new BlockingVault();
        var cache = new ConfigStoreCache(vault);

        vault.Release();
        await cache.InitializeAsync();
        Assert.True(cache.IsSettled);

        vault.Block();
        var reloading = cache.ReloadAsync();
        await vault.Entered.Task;

        Assert.True(cache.IsLoading);
        Assert.False(cache.IsSettled);

        vault.Release();
        await reloading;

        Assert.True(cache.IsSettled);
    }

    [Fact]
    public async Task AFailedFirstLoad_LeavesTheGateClosedRatherThanStuckLoading()
    {
        // Not initialised, so maintenance stays unavailable -- but not because a flag was left set.
        // The distinction matters: the app retries the load, and a stuck flag would keep the
        // section disabled even after a later attempt succeeded.
        var vault = new ThrowingVault();
        var cache = new ConfigStoreCache(vault);

        await Assert.ThrowsAsync<VaultCliException>(() => cache.InitializeAsync());

        Assert.False(cache.IsLoading);
        Assert.False(cache.IsSettled);
    }

    [Fact]
    public async Task AFailedReload_LeavesTheGateOpenAgain()
    {
        // The one that would strand the user. A re-initialise against a vault that has just become
        // unreachable must not disable vault maintenance permanently -- the store in memory is
        // still the one that was loaded, so the comparison is as valid as it was before.
        var vault = new ThrowingVault { Throw = false };
        var cache = new ConfigStoreCache(vault);

        await cache.InitializeAsync();

        vault.Throw = true;
        await Assert.ThrowsAsync<VaultCliException>(() => cache.ReloadAsync());

        Assert.False(cache.IsLoading);
        Assert.True(cache.IsSettled);
    }

    [Fact]
    public async Task TheLoadFlagIsAnnounced_SoAnOpenTabDoesNotStayDisabled()
    {
        // The Settings tab polls, but it also listens: without a notification on the way out of a
        // load, a tab already on screen would keep its buttons greyed until something unrelated
        // happened to refresh it.
        var cache = new ConfigStoreCache(new InMemoryVault());

        var announcements = 0;
        cache.PendingChanged += () => announcements++;

        await cache.InitializeAsync();

        Assert.True(announcements > 0);
    }

    /// <summary>
    /// Everything <see cref="IConfigVault"/> asks for, delegated to a real
    /// <see cref="InMemoryVault"/> — so the two fakes below only have to say how the *read* behaves,
    /// which is the only thing these tests are about.
    /// </summary>
    private abstract class DelegatingVault : IConfigVault
    {
        protected readonly InMemoryVault Inner = new();

        public VaultBackendKind Kind => Inner.Kind;
        public string VaultName => Inner.VaultName;
        public string? LastLoadWarning => Inner.LastLoadWarning;
        public IReadOnlyList<string> LastLoadRemovals => Inner.LastLoadRemovals;

        public abstract Task<ConfigStore> LoadAsync(CancellationToken ct = default);

        public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) => Inner.ProbeAsync(ct);
        public Task CreateVaultAsync(string vaultName, CancellationToken ct = default) => Inner.CreateVaultAsync(vaultName, ct);
        public Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default) => Inner.UseExistingVaultAsync(vaultName, ct);
        public void Forget() => Inner.Forget();
        public Task SaveAsync(ConfigStore store, CancellationToken ct = default) => Inner.SaveAsync(store, ct);
        public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) => Inner.RewriteAllAsync(store, ct);
        public Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default) => Inner.ListLiveItemsAsync(ct);
        public Task DeleteItemAsync(string itemId, CancellationToken ct = default) => Inner.DeleteItemAsync(itemId, ct);
    }

    /// <summary>A vault that will not return until told to, so the loading window can be observed.</summary>
    private sealed class BlockingVault : DelegatingVault
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Block()
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release() => _gate.TrySetResult();

        public override async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await _gate.Task;

            return await Inner.LoadAsync(ct);
        }
    }

    /// <summary>A vault that fails the read, for the paths where the gate must not latch.</summary>
    private sealed class ThrowingVault : DelegatingVault
    {
        public bool Throw { get; set; } = true;

        public override Task<ConfigStore> LoadAsync(CancellationToken ct = default) =>
            Throw
                ? throw new VaultCliException("The vault could not be read.")
                : Inner.LoadAsync(ct);
    }
}
