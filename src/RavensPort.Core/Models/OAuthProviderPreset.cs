namespace RavensPort.Core.Models;

/// <summary>
/// Prefill template for the Add-Credential form. Never persisted — once a credential is
/// created, it owns a full copy of the provider config and keeps working even if the
/// preset it came from changes later.
/// </summary>
public sealed record OAuthProviderPreset(
    string Name,
    string? Authority,
    string? AuthorizationEndpointHint,
    string? TokenEndpointHint,
    bool RequiresIdToken,
    bool UsesPkce,
    IReadOnlyList<string> DefaultScopes,
    string? HelpText)
{
    public static readonly OAuthProviderPreset Google = new(
        Name: "Google",
        Authority: "https://accounts.google.com",
        AuthorizationEndpointHint: null,
        TokenEndpointHint: null,
        RequiresIdToken: true,
        UsesPkce: true,
        DefaultScopes: ["openid", "email", "profile"],
        HelpText: "Register the OAuth client as a 'Desktop app' type in Google Cloud Console — " +
                   "only Desktop-type clients allow the arbitrary-port loopback redirect this app uses.");

    public static readonly OAuthProviderPreset Nextcloud = new(
        Name: "Nextcloud",
        Authority: null,
        AuthorizationEndpointHint: "https://<your-nextcloud>/apps/oauth2/authorize",
        TokenEndpointHint: "https://<your-nextcloud>/apps/oauth2/api/v1/token",
        RequiresIdToken: false,
        UsesPkce: false,
        DefaultScopes: [],
        HelpText: "Create an OAuth2 client under Nextcloud Settings → Security → OAuth2. " +
                   "Fill in the Authorization/Token endpoints using your instance's domain.");

    public static readonly OAuthProviderPreset Custom = new(
        Name: "Custom",
        Authority: null,
        AuthorizationEndpointHint: null,
        TokenEndpointHint: null,
        RequiresIdToken: false,
        UsesPkce: true,
        DefaultScopes: [],
        HelpText: "Enter the authorization and token endpoints (or an Authority for OIDC discovery) for any OAuth2 app.");

    public static readonly IReadOnlyList<OAuthProviderPreset> All = [Google, Nextcloud, Custom];
}
