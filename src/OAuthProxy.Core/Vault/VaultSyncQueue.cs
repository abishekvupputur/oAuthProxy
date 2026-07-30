using Microsoft.Extensions.Hosting;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Vault;

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
            + "They are lost if OAuthProxy exits first.");
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
