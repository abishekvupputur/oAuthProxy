using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

public sealed class CredentialRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>
    /// OAuth2 grant or static API key. Everything below splits along this line: the OAuth fields
    /// are meaningless for an API key, and <see cref="ApiKey"/> is meaningless for a grant.
    /// </summary>
    public CredentialKind Kind { get; set; } = CredentialKind.OAuth2;

    // Not `required` any more: an API-key credential has no OAuth client at all, and forcing
    // empty strings through a required initializer only obscured which fields actually matter.
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public List<string> Scopes { get; set; } = [];

    // Provider config, resolved from an OAuthProviderPreset at creation time and then
    // owned by the credential — presets are just prefill templates, not a persisted table.
    // IsGoogleProvider is set once from which preset was picked and never re-derived —
    // deciding "is this Google" from the editable Authority text field is fragile (a stray
    // edit, trailing slash, or blank would silently misroute the flow).
    public bool IsGoogleProvider { get; set; }
    public string? Authority { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public bool RequiresIdToken { get; set; }
    public bool UsesPkce { get; set; } = true;
    public string? ExtraAuthParams { get; set; }

    public TokenSet? Token { get; set; }

    /// <summary>
    /// The secret itself, for <see cref="CredentialKind.ApiKey"/>. Encrypted at rest with
    /// everything else in the store, and never redisplayed once saved — the editor treats a
    /// blank box as "keep the current key", exactly as it does for a client secret.
    /// </summary>
    public string? ApiKey { get; set; }

    // ---- Default placement ------------------------------------------------------------------
    //
    // Where this credential's secret normally goes. Two uses: it is what the "Test" button below
    // sends, and it prefills a route's credential entry so an "X-Api-Key" credential does not
    // have to be re-described on every route that uses it. The route still owns the placement it
    // actually forwards with — this is a default, not a constraint.

    public CredentialPlacement DefaultPlacement { get; set; } = CredentialPlacement.Header;
    public string DefaultParameterName { get; set; } = CredentialInjection.BearerHeader.Name;
    public string DefaultValuePrefix { get; set; } = CredentialInjection.BearerHeader.ValuePrefix;

    /// <summary>
    /// Optional URL that answers 200 to an authenticated GET, used to check the credential
    /// actually works.
    ///
    /// For an API key there is otherwise no way to tell a good key from a typo: unlike an OAuth
    /// flow, nothing validates it at the moment it is entered, so the first evidence of a wrong
    /// key is a 401 from a real request hours later.
    /// </summary>
    public string? TestEndpoint { get; set; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True once a refresh attempt has failed and the user needs to reconnect. Not persisted.</summary>
    [JsonIgnore]
    public bool NeedsReconnect { get; set; }

    /// <summary>Where this credential's secret goes by default.</summary>
    public CredentialInjection ToDefaultInjection() =>
        new(DefaultPlacement, DefaultParameterName, DefaultValuePrefix);

    /// <summary>
    /// True when the credential currently holds something usable — a stored API key, or an
    /// OAuth token. Says nothing about whether the upstream will accept it; that is what
    /// <see cref="TestEndpoint"/> is for.
    /// </summary>
    [JsonIgnore]
    public bool HasSecret => Kind == CredentialKind.ApiKey
        ? !string.IsNullOrEmpty(ApiKey)
        : Token is not null;

    /// <summary>The placement defaults a kind starts with, offered by the editor.</summary>
    public static CredentialInjection DefaultInjectionFor(CredentialKind kind) => kind == CredentialKind.ApiKey
        // Bearer is an OAuth convention; an API key almost always wants a bare value in a
        // bespoke header, which is what nearly every key-based API documents.
        ? new CredentialInjection(CredentialPlacement.Header, "X-Api-Key", "")
        : CredentialInjection.BearerHeader;
}
