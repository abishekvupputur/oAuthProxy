using Microsoft.Extensions.Hosting;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Vault;

/// <summary>Why the last sync attempt did not land, for the banner to explain.</summary>
public enum VaultSyncState
{
    /// <summary>Everything in memory is in the vault.</summary>
    Synced,

    /// <summary>A write is in flight.</summary>
    Syncing,

    /// <summary>The password manager is locked or signed out. Retrying.</summary>
    WaitingForUnlock,

    /// <summary>The vault refused the write for a reason unlocking will not fix.</summary>
    Failed,
}

/// <summary>
/// Writes the in-memory store out to the vault, whenever the vault will take it.
///
/// Exists because a password manager is locked a great deal of the time and the proxy has to keep
/// working regardless. Edits, token refreshes, and key rotations all complete against memory
/// immediately; this drains them to the vault behind the scenes and retries while the manager is
/// unavailable.
///
/// Coalescing falls out of the design rather than being engineered: the vault holds one whole
/// document, so the only thing worth writing is the newest state. Fifty edits during a lock cost
/// one write when it lifts, and a burst of edits while a write is in flight costs one more after.
///
/// Nothing is spilled to disk while waiting. That is the deliberate trade — a change that never
/// reaches the vault dies with the process — and it is why the exit path asks before discarding.
/// </summary>
public sealed class VaultSyncQueue : BackgroundService
{
    /// <summary>
    /// How long to wait after a change before writing. Long enough to fold a burst — adding a
    /// route fires several mutations in a row — short enough that a single edit feels immediate.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    /// <summary>Retry pacing while the manager is locked. Capped so a long lock is not a busy loop.</summary>
    private static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);

    private readonly ConfigStoreCache _configStoreCache;
    private readonly IConfigVault _vault;
    private readonly VaultGateService _gate;
    private readonly ActivityLog _activityLog;

    private readonly SemaphoreSlim _wakeUp = new(0);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TimeSpan _retryDelay = MinRetry;
    private bool _reportedWaiting;

    public VaultSyncQueue(
        ConfigStoreCache configStoreCache,
        IConfigVault vault,
        VaultGateService gate,
        ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _vault = vault;
        _gate = gate;
        _activityLog = activityLog;

        _configStoreCache.SyncRequested += Wake;
    }

    public VaultSyncState State { get; private set; } = VaultSyncState.Synced;

    /// <summary>The last failure, verbatim from the CLI where there was one.</summary>
    public string? LastError { get; private set; }

    public event Action<VaultSyncState>? StateChanged;

    /// <summary>Nudges the pump — after an unlock, or from the banner's "sync now".</summary>
    public void Wake()
    {
        if (_wakeUp.CurrentCount == 0) _wakeUp.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for something to do, but wake anyway on the retry timer so a lock that
                // lifts with no further edits still gets the pending change written.
                var idleFor = _configStoreCache.HasPendingChanges ? _retryDelay : Timeout.InfiniteTimeSpan;
                await _wakeUp.WaitAsync(idleFor, stoppingToken).ConfigureAwait(false);

                if (_configStoreCache.HasPendingChanges)
                {
                    await Task.Delay(Debounce, stoppingToken).ConfigureAwait(false);
                    await TrySyncAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The pump must outlive anything a vault can do to it. If it died, edits would
                // silently stop being saved with the UI still reporting them as pending forever.
                _activityLog.LogError("Vault sync failed unexpectedly", ex);
                Backoff();
            }
        }
    }

    /// <summary>
    /// Writes the newest state out. Returns true when nothing is left pending.
    /// </summary>
    public async Task<bool> TrySyncAsync(CancellationToken ct = default)
    {
        if (!_configStoreCache.HasPendingChanges) return true;

        // Before the gate has settled on a backend there is nowhere to write. Staying pending is
        // right — the changes are still good, and the first thing a chosen backend does is drain.
        if (_gate.Status.Selected == VaultBackendKind.None) return false;

        // Never write a store into a vault it did not come from. Choosing another password manager,
        // or another vault in the same one, repoints the gate immediately — and until the reload
        // that follows has landed, memory still holds the previous vault's configuration. Writing
        // it here would copy one vault's credentials, routes and keys into another, and then the
        // delete sweep would prune the destination to match. Staying pending is correct: the reload
        // replaces this store, and there is nothing worth saving from it.
        if (_configStoreCache.IsFromAnotherVault)
        {
            SetState(VaultSyncState.WaitingForUnlock);
            return false;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_configStoreCache.HasPendingChanges) return true;

            var (snapshot, version) = await _configStoreCache.SnapshotForSyncAsync(ct).ConfigureAwait(false);

            SetState(VaultSyncState.Syncing);

            await _vault.SaveAsync(snapshot, ct).ConfigureAwait(false);

            // Only up to the snapshot's version: an edit made while the write was in flight is
            // newer than what was written and has to stay pending.
            _configStoreCache.MarkSynced(version);

            _retryDelay = MinRetry;
            _reportedWaiting = false;
            LastError = null;

            SetState(_configStoreCache.HasPendingChanges ? VaultSyncState.Syncing : VaultSyncState.Synced);
            return !_configStoreCache.HasPendingChanges;
        }
        catch (Exception ex) when (ex is VaultLockedException or VaultCliException)
        {
            LastError = ex.Message;
            ReportWaitingOnce();
            Backoff();
            SetState(VaultSyncState.WaitingForUnlock);
            return false;
        }
        catch (VaultSaveException ex)
        {
            // The vault was reachable and said no — a concurrent write, or an item it would not
            // accept. Unlocking will not fix it, so this surfaces rather than retrying quietly.
            LastError = ex.Message;
            _activityLog.LogError("Could not save to the vault", ex);
            Backoff();
            SetState(VaultSyncState.Failed);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes every item and the note again, whatever the vault currently holds.
    ///
    /// Here rather than on the integrity service because this class owns the write lock: a full
    /// rewrite running beside the background pump would be two writers on one vault, which is the
    /// thing the lock exists to prevent.
    /// </summary>
    public Task<bool> RewriteAllAsync(TimeSpan timeout) => WriteAsync(rewriteEverything: true, timeout);

    /// <summary>
    /// An ordinary save, run now whether or not anything is pending.
    ///
    /// For putting back an item that has gone missing from the vault: a save creates whatever is
    /// not there and leaves the rest alone, so it costs one item rather than churning every entry
    /// the way a full rewrite does. Nothing is pending in that situation — memory and the note
    /// agree, it is the vault that does not — so the normal pump would never run.
    /// </summary>
    public Task<bool> WriteMissingAsync(TimeSpan timeout) => WriteAsync(rewriteEverything: false, timeout);

    private async Task<bool> WriteAsync(bool rewriteEverything, TimeSpan timeout)
    {
        if (_gate.Status.Selected == VaultBackendKind.None) return false;

        // Never write a store into a vault it did not come from. Choosing another password manager,
        // or another vault in the same one, repoints the gate immediately — and until the reload
        // that follows has landed, memory still holds the previous vault's configuration. Writing
        // it here would copy one vault's credentials, routes and keys into another, and then the
        // delete sweep would prune the destination to match. Staying pending is correct: the reload
        // replaces this store, and there is nothing worth saving from it.
        if (_configStoreCache.IsFromAnotherVault)
        {
            SetState(VaultSyncState.WaitingForUnlock);
            return false;
        }

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            var (snapshot, version) = await _configStoreCache.SnapshotForSyncAsync(cts.Token).ConfigureAwait(false);

            SetState(VaultSyncState.Syncing);

            if (rewriteEverything)
            {
                await _vault.RewriteAllAsync(snapshot, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await _vault.SaveAsync(snapshot, cts.Token).ConfigureAwait(false);
            }

            _configStoreCache.MarkSynced(version);
            _retryDelay = MinRetry;
            _reportedWaiting = false;
            LastError = null;

            _activityLog.Log(rewriteEverything
                ? "VAULT rewrote every item and the configuration from memory"
                : "VAULT wrote memory to the vault, restoring anything missing from it");

            SetState(_configStoreCache.HasPendingChanges ? VaultSyncState.Syncing : VaultSyncState.Synced);
            return true;
        }
        catch (Exception ex) when (ex is VaultLockedException or VaultCliException or VaultSaveException
                                       or OperationCanceledException)
        {
            LastError = ex.Message;
            _activityLog.LogError("Could not write to the vault", ex);
            SetState(ex is VaultSaveException ? VaultSyncState.Failed : VaultSyncState.WaitingForUnlock);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Pushes now and waits, for shutdown and for the banner's "sync now". Returns false when
    /// changes are still pending afterwards — which on exit means they are about to be lost.
    /// </summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            return await TrySyncAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until no write is in flight, then returns. Nothing new is started.
    ///
    /// Called before the gate is repointed — by disconnecting, or by choosing another vault. A save
    /// takes seconds of subprocess time, and the backend it lands on is resolved as it goes, so a
    /// write still running while the gate moves can finish against a vault it was never meant for.
    /// That is not hypothetical: an activity log shows a 1Password item being written three seconds
    /// *after* Disconnect was pressed, with a Proton Pass vault being connected moments later.
    ///
    /// Returns false when the write did not finish inside the timeout, so the caller can decide
    /// whether to proceed — it should not, but pausing the app forever is worse than saying so.
    /// </summary>
    public async Task<bool> WaitForQuietAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _activityLog.Log(
                "VAULT a save was still running when the password manager was about to change — "
                + "waited and gave up, so the change was not made");

            return false;
        }

        // Taken and released immediately: the point is to observe that nothing holds it, not to
        // keep it. Holding it would deadlock the disconnect against the queue's own pump.
        _writeLock.Release();
        return true;
    }

    /// <summary>
    /// Said once per lock rather than once per retry. The pump retries every couple of seconds at
    /// first, and logging each attempt would bury everything else in the activity log.
    /// </summary>
    private void ReportWaitingOnce()
    {
        if (_reportedWaiting) return;
        _reportedWaiting = true;

        _activityLog.Log(
            $"VAULT {VaultLockGuidance.DisplayName(_gate.Status.Selected)} is locked — "
            + "changes are being kept in memory and will be saved when it is unlocked. "
            + "They are lost if RavensPort exits first.");
    }

    private void Backoff() =>
        _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetry.Ticks));

    private void SetState(VaultSyncState state)
    {
        if (State == state) return;

        State = state;
        StateChanged?.Invoke(state);
    }

    public override void Dispose()
    {
        _configStoreCache.SyncRequested -= Wake;
        base.Dispose();
    }
}
