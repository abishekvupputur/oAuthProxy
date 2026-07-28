using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Storage;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshDueCredentialsAsync(stoppingToken);
        }
    }

    private async Task RefreshDueCredentialsAsync(CancellationToken ct)
    {
        // Snapshot the list so edits to Credentials during iteration (e.g. a delete from
        // the UI) can't throw a collection-modified error.
        var dueCredentials = configStoreCache.Current.Credentials
            .Where(c => c.Token is not null && c.Token.IsExpiringWithin(ExpiryWindow))
            .ToList();

        if (dueCredentials.Count == 0) return;

        var anyRefreshed = false;
        foreach (var credential in dueCredentials)
        {
            try
            {
                activityLog.Log($"REFRESH '{credential.Name}' expiring soon — refreshing token");
                var refreshed = await oAuth2Service.RefreshAsync(credential, ct);
                anyRefreshed |= refreshed is not null;
                activityLog.Log(refreshed is not null
                    ? $"REFRESH '{credential.Name}' OK — new expiry {refreshed.ExpiresAtUtc.ToLocalTime():g}"
                    : $"REFRESH '{credential.Name}' FAILED — reconnect required");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh token for credential {CredentialName}", credential.Name);
                activityLog.LogError($"REFRESH '{credential.Name}' threw", ex);
                credential.NeedsReconnect = true;
            }
        }

        if (anyRefreshed)
        {
            await configStoreCache.SaveAsync(ct);
        }
    }
}
