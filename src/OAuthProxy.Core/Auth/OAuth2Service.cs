using System.Collections.Concurrent;
using IdentityModel.Client;
using IdentityModel.Jwk;
using IdentityModel.OidcClient;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Auth;

public sealed record AuthorizationOutcome(bool Success, string? Error, string? ErrorDescription);

/// <summary>
/// Single entry point ViewModels/TokenRefreshService call regardless of provider. Google
/// credentials delegate to GoogleOAuthService (Google's own official client library);
/// every other provider (Nextcloud, Custom) goes through the generic OidcClient path here,
/// branching only on whether the credential has an Authority (OIDC discovery) or manual
/// AuthorizationEndpoint/TokenEndpoint.
/// </summary>
public sealed class OAuth2Service(GoogleOAuthService googleOAuthService, ActivityLog activityLog)
{
    // Guards against the background refresh loop and a manual "Refresh Now" UI action
    // racing a refresh for the same credential.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    private static bool IsGoogle(CredentialRecord credential) => credential.IsGoogleProvider;

    public async Task<AuthorizationOutcome> StartAuthorizationAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        if (IsGoogle(credential))
        {
            return await googleOAuthService.StartAuthorizationAsync(credential, ct);
        }

        // The browser owns its HttpListener per-invocation and always releases it, so there
        // is nothing to dispose here.
        var browser = new LoopbackBrowser();
        var options = BuildOptions(credential, browser);

        var request = new LoginRequest();
        if (!string.IsNullOrWhiteSpace(credential.ExtraAuthParams))
        {
            request.FrontChannelExtraParameters ??= new Parameters();
            foreach (var pair in ParseExtraParams(credential.ExtraAuthParams))
            {
                request.FrontChannelExtraParameters.Add(pair.Key, pair.Value);
            }
        }

        var client = new OidcClient(options);
        var result = await client.LoginAsync(request, ct);

        if (result.IsError)
        {
            return new AuthorizationOutcome(false, result.Error, result.ErrorDescription);
        }

        credential.Token = new TokenSet(
            result.AccessToken,
            result.RefreshToken,
            result.AccessTokenExpiration,
            "Bearer",
            DateTimeOffset.UtcNow);
        credential.NeedsReconnect = false;

        return new AuthorizationOutcome(true, null, null);
    }

    public async Task<TokenSet?> RefreshAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        var refreshLock = _refreshLocks.GetOrAdd(credential.Id, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(ct);
        try
        {
            if (credential.Token?.RefreshToken is null)
            {
                return null;
            }

            if (IsGoogle(credential))
            {
                return await googleOAuthService.RefreshAsync(credential, ct);
            }

            var refreshToken = credential.Token.RefreshToken;
            var options = BuildOptions(credential, browser: null);
            var client = new OidcClient(options);
            var result = await client.RefreshTokenAsync(refreshToken, null, null, ct);

            if (result.IsError)
            {
                // Same fix as GoogleOAuthService: the provider's actual error/description was
                // being discarded here, leaving only "reconnect may be required" in the UI
                // with no way to find out why (expired refresh token, revoked grant, endpoint
                // unreachable, etc).
                activityLog.Log($"REFRESH '{credential.Name}' provider error: {result.Error} {result.ErrorDescription}".Trim());
                credential.NeedsReconnect = true;
                return null;
            }

            // Most providers omit refresh_token on subsequent refreshes — keep the old one.
            var newRefreshToken = string.IsNullOrEmpty(result.RefreshToken) ? refreshToken : result.RefreshToken;

            var newToken = new TokenSet(
                result.AccessToken,
                newRefreshToken,
                result.AccessTokenExpiration,
                "Bearer",
                DateTimeOffset.UtcNow);

            credential.Token = newToken;
            credential.NeedsReconnect = false;
            return newToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static OidcClientOptions BuildOptions(CredentialRecord credential, LoopbackBrowser? browser)
    {
        var options = new OidcClientOptions
        {
            ClientId = credential.ClientId,
            ClientSecret = credential.ClientSecret,
            Scope = string.Join(' ', credential.Scopes),
            Policy = new Policy(),
            // Defaults to true, which makes OidcClient call the userinfo endpoint after the
            // token exchange and throw "No userinfo endpoint specified" for plain-OAuth2
            // providers like Nextcloud that have none. We only ever want the access token —
            // the profile claims are never read — so skip that call entirely.
            LoadProfile = false,
        };

        if (browser is not null)
        {
            options.Browser = browser;
            options.RedirectUri = browser.RedirectUri;
        }

        if (!string.IsNullOrWhiteSpace(credential.Authority))
        {
            options.Authority = credential.Authority;
        }
        else
        {
            options.ProviderInformation = new ProviderInformation
            {
                IssuerName = credential.AuthorizationEndpoint ?? credential.TokenEndpoint!,
                KeySet = new JsonWebKeySet(),
                AuthorizeEndpoint = credential.AuthorizationEndpoint,
                TokenEndpoint = credential.TokenEndpoint,
            };
        }

        // CredentialRecord.UsesPkce is deliberately not consulted here. IdentityModel.OidcClient 6
        // always sends a code_challenge and exposes no switch to turn that off, so this path is
        // unconditionally PKCE-protected. Only the Google flow honours the flag, and the UI now
        // shows the checkbox only for Google rather than implying a setting that does nothing.
        if (!credential.RequiresIdToken)
        {
            // Plain-OAuth2 providers (e.g. Nextcloud) don't return an id_token at all —
            // skip identity-token validation entirely rather than requiring one.
            options.IdentityTokenValidator = new NoValidationIdentityTokenValidator();
        }

        return options;
    }

    /// <summary>
    /// Parses the "a=1&amp;b=2" extra-parameters field. Values are percent-decoded, because a
    /// user copying a parameter out of a provider's docs gets it in encoded form — leaving it
    /// encoded meant it was encoded a second time on the wire and the provider saw a literal
    /// "%2F" where a "/" was intended.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> ParseExtraParams(string raw)
    {
        foreach (var segment in raw.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                yield return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(parts[0].Trim()),
                    Uri.UnescapeDataString(parts[1].Trim()));
            }
        }
    }
}
