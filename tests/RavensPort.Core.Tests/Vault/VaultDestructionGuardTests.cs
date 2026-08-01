using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The rules that stand between a bad moment and a user's credentials being gone.
///
/// Deleting an item is the only irreversible thing RavensPort does to a vault, and it is decided by
/// subtraction: anything in the vault that the store in memory does not account for is unwanted.
/// That inference is only sound when the store is a complete, current picture of *this* vault.
/// These pin every way it can fail to be one.
///
/// The bug that prompted them is in the log of an ordinary startup: <c>op item get</c> returned
/// "connecting to desktop app timed out", the provider read that as "the item was deleted", the
/// record was dropped from the store, and the next save then deleted the item from the vault for
/// real. A transient IPC hiccup destroyed a credential.
/// </summary>
public class VaultDestructionGuardTests : IDisposable
{
    private const string ClientSecret = "SENTINEL-CLIENT-SECRET";

    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-guard-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-guard-logs-{Guid.NewGuid()}");
    private readonly string _stub;

    public VaultDestructionGuardTests()
    {
        Directory.CreateDirectory(_stubDir);
        _stub = Path.Combine(_stubDir, "op.exe");
        File.WriteAllText(_stub, "");
    }

    private ActivityLog Log() => new(_logPath);

    private OnePasswordVaultProvider Provider(ICliRunner runner) => new(runner, Log(), _stub);

    private static ConfigStore StoreWithOneCredential() =>
        new()
        {
            Credentials =
            {
                new CredentialRecord
                {
                    Name = "Example",
                    Kind = CredentialKind.ApiKey,
                    ApiKey = ClientSecret,
                },
            },
        };

    // ---- An unreachable item is not a deleted item -------------------------------------------

    [Fact]
    public async Task ALoadFailsLoudly_WhenAnItemCannotBeRead()
    {
        // The reported failure, verbatim. It must stop the load rather than quietly produce a
        // store with the credential missing -- that store is what the delete sweep acts on.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        var provider = Provider(runner);
        await provider.SaveAsync(StoreWithOneCredential());

        var failing = new FailingItemGetRunner(runner)
        {
            Stderr = "[ERROR] error initializing client: connecting to desktop app: "
                     + "connecting to desktop app timed out, make sure it is installed, running and "
                     + "CLI integration is enabled",
            PassThroughFirstCalls = 1,
        };

        await Assert.ThrowsAsync<VaultCliException>(() => Provider(failing).LoadAsync());
    }

    [Fact]
    public async Task AnUnreadableItemIsRetried_BeforeTheLoadIsGivenUpOn()
    {
        // The desktop app being busy is the common case, and a read is idempotent -- so a hiccup on
        // the first attempt must not cost the user a failed startup.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        var provider = Provider(runner);
        await provider.SaveAsync(StoreWithOneCredential());

        var flaky = new FailingItemGetRunner(runner)
        {
            Stderr = "connecting to desktop app timed out",
            FailuresBeforeSucceeding = 2,
            PassThroughFirstCalls = 1,
        };

        var loaded = await Provider(flaky).LoadAsync();

        // Recovered rather than dropped, which is the whole point.
        Assert.Single(loaded.Credentials);
        Assert.Equal(ClientSecret, loaded.Credentials[0].ApiKey);
        Assert.True(flaky.ItemGetCalls >= 3, $"expected a retry, saw {flaky.ItemGetCalls} item get call(s)");
    }

    [Fact]
    public async Task AnItemOnePasswordSaysIsGone_IsStillTreatedAsRemoved()
    {
        // The other direction has to keep working: a genuinely deleted item is a real removal, and
        // reporting it is how the user finds out a credential stopped working.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        var provider = Provider(runner);
        await provider.SaveAsync(StoreWithOneCredential());

        var missing = new FailingItemGetRunner(runner)
        {
            Stderr = "\"item-2\" isn't an item. Specify the item with its UUID, name, or domain.",
            PassThroughFirstCalls = 1,
        };

        var reader = Provider(missing);
        var reloaded = await reader.LoadAsync();

        Assert.Empty(reloaded.Credentials);
        Assert.NotEmpty(reader.LastLoadRemovals);

        // And it must not have been retried: 1Password gave a definitive answer, so repeating the
        // question would only slow a load down on the one path that is already bad news.
        Assert.Equal(1, missing.ItemGetCalls);
    }

    // ---- Never sweep from a baseline this session did not establish --------------------------

    [Fact]
    public async Task SavingWithoutHavingLoaded_DeletesNothing()
    {
        // The scenario that would empty a vault: a save runs against a store that is empty because
        // nothing was ever read, not because the user removed anything. Subtraction would call
        // every item in the vault unwanted.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        await Provider(runner).SaveAsync(StoreWithOneCredential());
        var afterFirstSave = fake.Items.Count;

        // A brand new provider instance -- no load, so no baseline -- saving an empty store.
        await Provider(runner).SaveAsync(new ConfigStore());

        Assert.Empty(runner.CallsMatching("item", "delete"));
        Assert.Equal(afterFirstSave, fake.Items.Count);
    }

    [Fact]
    public async Task SavingAfterAFullLoad_StillRemovesWhatTheUserRemoved()
    {
        // The guard must not become "never delete anything", or a credential the user deleted
        // would stay in their vault forever.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        var provider = Provider(runner);
        await provider.SaveAsync(StoreWithOneCredential());

        var loaded = await provider.LoadAsync();
        Assert.Single(loaded.Credentials);

        loaded.Credentials.Clear();
        await provider.SaveAsync(loaded);

        Assert.NotEmpty(runner.CallsMatching("item", "delete"));
    }

    [Fact]
    public async Task Forgetting_StopsWritingAltogether()
    {
        // Disconnecting, or moving to another vault. This is the mechanism that cost a user nine
        // items: a save issued after the disconnect found no vault, re-probed, adopted whatever it
        // discovered, and wrote a configuration belonging to somewhere else into it.
        //
        // It now refuses. Loudly, rather than silently doing nothing, because a save that quietly
        // evaporates is its own kind of data loss.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        var provider = Provider(runner);
        await provider.SaveAsync(StoreWithOneCredential());
        await provider.LoadAsync();

        var beforeCount = fake.Items.Count;
        provider.Forget();

        await Assert.ThrowsAsync<VaultSaveException>(() => provider.SaveAsync(new ConfigStore()));

        Assert.Empty(runner.CallsMatching("item", "delete"));
        Assert.Equal(beforeCount, fake.Items.Count);
    }

    [Fact]
    public async Task WritingResumes_OnceAVaultIsChosenAgain()
    {
        // The lockout must not be a one-way door: choosing a vault is what re-opens writing, and
        // reconnecting after a disconnect has to work.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        var provider = Provider(runner);
        await provider.SaveAsync(StoreWithOneCredential());

        provider.Forget();
        await Assert.ThrowsAsync<VaultSaveException>(() => provider.SaveAsync(new ConfigStore()));

        await provider.UseExistingVaultAsync(VaultConstants.VaultName);

        // No longer refused.
        await provider.SaveAsync(StoreWithOneCredential());
    }

    [Fact]
    public async Task ASaveCarryingAnotherVaultsNote_DeletesNothing()
    {
        // The 15:20 incident in miniature. A store whose records this vault has never heard of gets
        // saved into it. Under the old sweep every item here was unaccounted for and therefore
        // unwanted; deletion is now restricted to items this vault's own note pointed at, so a note
        // from elsewhere can match nothing.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();

        var provider = Provider(runner);
        await provider.SaveAsync(StoreWithOneCredential());
        await provider.LoadAsync();

        var beforeCount = fake.Items.Count;

        // Different record ids entirely -- what another vault's configuration looks like from here.
        var stranger = new ConfigStore
        {
            Credentials =
            {
                new CredentialRecord { Name = "Elsewhere", Kind = CredentialKind.ApiKey, ApiKey = "other" },
            },
        };

        await provider.SaveAsync(stranger);

        Assert.Empty(runner.CallsMatching("item", "delete"));
        Assert.True(fake.Items.Count >= beforeCount, "the incoming configuration removed items it had never seen");
    }

    /// <summary>
    /// Wraps the fake CLI and makes <c>item get</c> fail, so the difference between "cannot read"
    /// and "not there" can be exercised without touching the shared fake.
    /// </summary>
    private sealed class FailingItemGetRunner(ICliRunner inner) : ICliRunner
    {
        public string Stderr { get; init; } = "";

        /// <summary>Zero means fail every time it is reached.</summary>
        public int FailuresBeforeSucceeding { get; init; }

        /// <summary>
        /// Item fetches to let through untouched before failing any.
        ///
        /// One, in every test here. <c>LoadAsync</c> fetches the config note first and the secrets
        /// it indexes afterwards, so failing from the very first call breaks the note instead —
        /// which returns an empty store by a completely different path and would have made these
        /// tests pass while proving nothing about secrets.
        /// </summary>
        public int PassThroughFirstCalls { get; init; }

        /// <summary>Attempts against secret items, which is what the retry rule is about.</summary>
        public int ItemGetCalls { get; private set; }

        public Task<CliResult> RunAsync(
            string exePath, IReadOnlyList<string> args, string? stdin = null,
            IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            if (args is ["item", "get", ..])
            {
                if (_passedThrough < PassThroughFirstCalls)
                {
                    _passedThrough++;
                    return inner.RunAsync(exePath, args, stdin, env, timeout, ct);
                }

                ItemGetCalls++;

                if (FailuresBeforeSucceeding == 0 || ItemGetCalls <= FailuresBeforeSucceeding)
                {
                    return Task.FromResult(new CliResult(1, "", Stderr));
                }
            }

            return inner.RunAsync(exePath, args, stdin, env, timeout, ct);
        }

        private int _passedThrough;

        public Task<CliResult> RunStreamingAsync(
            string exePath, IReadOnlyList<string> args, Action<string> onOutputLine,
            IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null,
            CancellationToken ct = default)
            => inner.RunStreamingAsync(exePath, args, onOutputLine, env, timeout, ct);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stubDir)) Directory.Delete(_stubDir, recursive: true);
        if (Directory.Exists(_logPath)) Directory.Delete(_logPath, recursive: true);

        GC.SuppressFinalize(this);
    }
}
