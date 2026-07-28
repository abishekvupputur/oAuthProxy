using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Auth;

/// <summary>
/// Google-specific OAuth flow using Google's own official client library
/// (GoogleWebAuthorizationBroker) instead of the generic IdentityModel.OidcClient path —
/// gets Google's own loopback handling, PKCE toggle, and refresh support "for free" and
/// keeps up with any Google-side auth quirks automatically. Every other provider
/// (Nextcloud, Custom) still goes through the generic OAuth2Service/OidcClient path.
/// </summary>
public sealed class GoogleOAuthService
{
    public const string GoogleAuthority = "https://accounts.google.com";

    // Fixed port so the redirect URI is stable and displayable/registerable in Google Cloud
    // Console, instead of a fresh random port every attempt.
    private const int RedirectPort = 51004;
    public static readonly string RedirectUri = new FixedPortGoogleCodeReceiver(RedirectPort).RedirectUri;

    public async Task<AuthorizationOutcome> StartAuthorizationAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        var initializer = new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = credential.ClientId, ClientSecret = credential.ClientSecret },
            DataStore = new NoOpDataStore(),
        };

        try
        {
            var userCredential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                initializer,
                credential.Scopes,
                "user",
                credential.UsesPkce,
                ct,
                new NoOpDataStore(),
                new FixedPortGoogleCodeReceiver(RedirectPort));

            ApplyToken(credential, userCredential.Token);
            return new AuthorizationOutcome(true, null, null);
        }
        catch (Exception ex)
        {
            return new AuthorizationOutcome(false, "google_auth_error", ex.Message);
        }
    }

    public async Task<TokenSet?> RefreshAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        if (credential.Token?.RefreshToken is not { } refreshToken)
        {
            return null;
        }

        var initializer = new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = credential.ClientId, ClientSecret = credential.ClientSecret },
            DataStore = new NoOpDataStore(),
        };

        using var flow = new GoogleAuthorizationCodeFlow(initializer);

        try
        {
            var tokenResponse = await flow.RefreshTokenAsync("user", refreshToken, ct);
            ApplyToken(credential, tokenResponse);
            return credential.Token;
        }
        catch
        {
            credential.NeedsReconnect = true;
            return null;
        }
    }

    private static void ApplyToken(CredentialRecord credential, TokenResponse token)
    {
        var expiresAtUtc = token.ExpiresInSeconds.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds.Value)
            : DateTimeOffset.UtcNow.AddHours(1);

        // Google often omits refresh_token on subsequent refreshes — keep the old one.
        var refreshToken = string.IsNullOrEmpty(token.RefreshToken) ? credential.Token?.RefreshToken : token.RefreshToken;

        credential.Token = new TokenSet(token.AccessToken, refreshToken, expiresAtUtc, "Bearer", DateTimeOffset.UtcNow);
        credential.NeedsReconnect = false;
    }
}
