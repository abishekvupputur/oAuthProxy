using System.Text.Json.Serialization;

namespace OAuthProxy.Core.Models;

public sealed class CredentialRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
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
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True once a refresh attempt has failed and the user needs to reconnect. Not persisted.</summary>
    [JsonIgnore]
    public bool NeedsReconnect { get; set; }
}
