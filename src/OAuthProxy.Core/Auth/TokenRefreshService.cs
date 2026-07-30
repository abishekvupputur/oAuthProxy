using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Auth;

/// <summary>
/// Scans all stored credentials every minute and refreshes any token expiring within
/// 10 minutes. Runs for the lifetime of the host (tied to App.OnStartup/OnExit).
/// </summary>
public sealed class TokenRefreshService(
    ConfigStoreCache configStoreCache,
    OAuth2Service oAuth2Service,
    ActivityLog activityLog,
    ILogger<TokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// Consecutive failures per credential, used to space out retries. A grant that has been
    /// revoked matches the "expiring soon" filter forever, so without this the loop hit the
    /// provider every 60 seconds indefinitely — enough to get rate-limited or flagged, and
    /// enough to bury the log in identical errors.
    ///
    /// Concurrent because ResetBackoff is called from the WPF dispatcher thread while the loop
    /// reads and writes it on a thread-pool thread.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (int Failures, DateTimeOffset NextAttemptUtc)> _backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RefreshDueCredentialsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The whole tick is wrapped, not just the per-credential work. SaveAsync used
                // to sit outside the inner try, so a locked or full disk threw straight out of
                // ExecuteAsync — and BackgroundService's default StopHost behavior then tore
                // down Kestrel. The tray icon stayed put while every proxied request died, with
                // nothing on screen to say why.
                logger.LogError(ex, "Token refresh tick failed");
                activityLog.LogError("REFRESH tick failed — will retry next minute", ex);
            }
        }
    }

    /// <summary>One pass of the loop. Internal so its vault gate can be tested without a 60s wait.</summary>
    internal async Task RefreshDueCredentialsAsync(CancellationToken ct)
    {
        // Nothing is attempted while the vault cannot record the outcome. A refresh rotates the
        // refresh token at the provider, and a rotated token that only exists in memory is a grant
        // lost for good the moment the app restarts — the user would have to reconnect from
        // scratch, having done nothing wrong. Not refreshing leaves the old token valid and
        // durable, so the cost is an access token that expires until the vault reopens.
        //
        // Returning before the scan matters: _backoff must not be touched. A credential that was
        // never attempted has not failed, and recording one here would push a perfectly healthy
        // grant into an hour-long backoff for something that was never its fault.
        if (configStoreCache.Access == VaultAccess.ReadOnly)
        {
            ReportPausedOnce();
            return;
        }

        _reportedPaused = false;

        var now = DateTimeOffset.UtcNow;

        // Snapshot the list so edits to Credentials during iteration (e.g. a delete from
        // the UI) can't throw a collection-modified error.
        var dueCredentials = configStoreCache.Current.Credentials
            .Where(c => c.Token is not null
                        && c.Token.RefreshToken is not null
                        && c.Token.IsExpiringWithin(ExpiryWindow)
                        && IsDueForAttempt(c.Id, now))
            .ToList();

        if (dueCredentials.Count == 0) return;

        var anyRefreshed = false;
        foreach (var credential in dueCredentials)
        {
            try
            {
                activityLog.Log($"REFRESH '{credential.Name}' expiring soon — refreshing token");
                var refreshed = await oAuth2Service.RefreshAsync(credential, ct);

                if (refreshed is not null)
                {
                    anyRefreshed = true;
                    _backoff.TryRemove(credential.Id, out _);
                    activityLog.Log($"REFRESH '{credential.Name}' OK — new expiry {refreshed.ExpiresAtUtc.ToLocalTime():g}");
                }
                else
                {
                    var retryAt = RecordFailure(credential.Id, now);
                    activityLog.Log($"REFRESH '{credential.Name}' FAILED — reconnect required "
                                    + $"(next automatic attempt {retryAt.ToLocalTime():g})");
                }
            }
            catch (Exception ex)
            {
                var retryAt = RecordFailure(credential.Id, now);
                logger.LogWarning(ex, "Failed to refresh token for credential {CredentialName}", credential.Name);
                activityLog.LogError($"REFRESH '{credential.Name}' threw (next attempt {retryAt.ToLocalTime():g})", ex);
                credential.NeedsReconnect = true;
            }
        }

        if (anyRefreshed)
        {
            await configStoreCache.SaveAsync(ct);
        }

        PruneBackoffForDeletedCredentials();
    }

    /// <summary>
    /// Says once per lock that refreshing has stopped, and how long there is before it shows.
    /// The loop ticks every 60 seconds, so saying it each time would bury the activity log — and
    /// the one thing worth knowing is when a route is about to start failing.
    /// </summary>
    private void ReportPausedOnce()
    {
        if (_reportedPaused) return;
        _reportedPaused = true;

        var soonest = configStoreCache.Current.Credentials
            .Where(c => c.Token is not null)
            .Select(c => c.Token!.ExpiresAtUtc)
            .DefaultIfEmpty()
            .Min();

        var detail = soonest == default
            ? "no OAuth credentials are affected"
            : $"the first token expires {soonest.ToLocalTime():g}";

        activityLog.Log($"REFRESH paused — the vault is locked, so tokens are not being refreshed ({detail})");
    }

    private bool _reportedPaused;

    private bool IsDueForAttempt(Guid credentialId, DateTimeOffset now) =>
        !_backoff.TryGetValue(credentialId, out var state) || now >= state.NextAttemptUtc;

    /// <summary>Doubles the wait after each consecutive failure, capped at an hour.</summary>
    private DateTimeOffset RecordFailure(Guid credentialId, DateTimeOffset now)
    {
        var failures = _backoff.TryGetValue(credentialId, out var state) ? state.Failures + 1 : 1;

        var delayTicks = Math.Min(
            InitialBackoff.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 10)),
            MaxBackoff.Ticks);

        var nextAttempt = now + TimeSpan.FromTicks(delayTicks);
        _backoff[credentialId] = (failures, nextAttempt);
        return nextAttempt;
    }

    /// <summary>Keeps the dictionary from growing across a long uptime of add/delete cycles.</summary>
    private void PruneBackoffForDeletedCredentials()
    {
        if (_backoff.Count == 0) return;

        var live = configStoreCache.Current.Credentials.Select(c => c.Id).ToHashSet();
        foreach (var id in _backoff.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _backoff.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Lets the UI's "Connect"/"Refresh now" clear a credential's backoff immediately, so a
    /// user who just fixed the underlying problem is not made to wait out the timer.
    /// </summary>
    public void ResetBackoff(CredentialRecord credential) => _backoff.TryRemove(credential.Id, out _);
}
