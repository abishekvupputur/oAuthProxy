using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.Core.Auth;

/// <summary>
/// Single place that answers "give me a usable access token for this credential, right now".
///
/// The periodic <see cref="TokenRefreshService"/> alone was not enough: it ticks once a minute,
/// so a machine waking from sleep, or a token whose real lifetime is shorter than advertised,
/// left the proxy forwarding a stale token and the caller seeing a bare 401 with no recovery.
/// Refreshing on demand at the moment of use closes that window.
/// </summary>
public sealed class AccessTokenProvider(
    ConfigStoreCache configStoreCache,
    OAuth2Service oAuth2Service,
    ActivityLog activityLog)
{
    /// <summary>
    /// Refresh margin for the on-demand path. Deliberately small — the periodic loop handles
    /// the comfortable 10-minute-ahead case, and this only catches what slipped through.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    public async ValueTask<string?> GetAccessTokenAsync(Guid credentialId, CancellationToken ct = default)
    {
        var credential = configStoreCache.GetCredential(credentialId);
        if (credential?.Token is not { } token) return null;

        if (!token.IsExpiringWithin(RefreshMargin))
        {
            return token.AccessToken;
        }

        // Nothing to refresh with, or a previous attempt already established the grant is
        // dead. Hand back what we have rather than hammering the provider on every request —
        // a 401 from upstream is the honest outcome at that point.
        if (token.RefreshToken is null || credential.NeedsReconnect)
        {
            return token.AccessToken;
        }

        return await RefreshOnDemandAsync(credential, ct);
    }

    private async ValueTask<string?> RefreshOnDemandAsync(CredentialRecord credential, CancellationToken ct)
    {
        try
        {
            // OAuth2Service serializes refreshes per credential, so a burst of concurrent
            // proxied requests produces exactly one token exchange; the rest wait and then
            // observe the already-refreshed token below.
            activityLog.Log($"REFRESH '{credential.Name}' expired mid-use — refreshing before forwarding");
            var refreshed = await oAuth2Service.RefreshAsync(credential, ct);

            if (refreshed is null)
            {
                activityLog.Log($"REFRESH '{credential.Name}' FAILED on demand — reconnect required");
                return credential.Token?.AccessToken;
            }

            await configStoreCache.SaveAsync(ct);
            return refreshed.AccessToken;
        }
        catch (Exception ex)
        {
            // A proxied request must never be taken down by a refresh failure; forward the
            // stale token and let the upstream give its own verdict.
            activityLog.LogError($"On-demand refresh of '{credential.Name}' threw", ex);
            return credential.Token?.AccessToken;
        }
    }
}
