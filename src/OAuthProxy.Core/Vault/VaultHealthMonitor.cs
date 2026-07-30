using Microsoft.Extensions.Hosting;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Vault;

/// <summary>
/// Watches whether the password manager is still reachable, and puts the app into read-only mode
/// when it is not.
///
/// Needed because a lock is silent: nothing tells the app that the user's 1Password just timed
/// out. Without this the first sign would be a save failing halfway, or worse, a token refresh
/// succeeding at the provider with nowhere to record the rotated refresh token.
///
/// Kestrel deliberately keeps running through a lock. Tearing it down would break every in-flight
/// agent session over something the user often fixes in five seconds, and serving from memory is
/// not the same as storing anything — the no-local-storage rule is intact either way.
/// </summary>
public sealed class VaultHealthMonitor(
    ConfigStoreCache configStoreCache,
    IConfigVault vault,
    VaultGateService gate,
    ActivityLog activityLog) : BackgroundService
{
    /// <summary>
    /// Long enough that the probe is not a constant background subprocess, short enough that the
    /// UI does not stay wrong for long after an unlock.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A monitor that could take the host down would be worse than no monitor: the
                // proxy would stop serving because the app failed to notice a lock.
                activityLog.LogError("Vault health check failed", ex);
            }
        }
    }

    /// <summary>
    /// Re-probes now. Called on a save failure too, so the UI flips immediately rather than at the
    /// next tick — a user who just watched an edit fail should not then have to wait a minute to
    /// find out why.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        // Before the gate has chosen a backend there is nothing to monitor, and probing the
        // placeholder would report a healthy vault that does not exist.
        if (gate.Status.Selected == VaultBackendKind.None) return;

        var status = await vault.ProbeAsync(ct);

        var access = status.IsReady ? VaultAccess.Writable : VaultAccess.ReadOnly;
        if (access == configStoreCache.Access) return;

        activityLog.Log(access == VaultAccess.ReadOnly
            ? $"VAULT {VaultLockGuidance.DisplayName(vault.Kind)} is locked or signed out — "
              + "editing and token refresh are paused until it is available again"
            : $"VAULT {VaultLockGuidance.DisplayName(vault.Kind)} is available again — editing and "
              + "token refresh resumed");

        configStoreCache.SetAccess(access);
    }
}
