namespace OAuthProxy.Core.Models;

public sealed class RouteMapping
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string PathPrefix { get; set; }
    public Guid UpstreamId { get; set; }
    public Guid CredentialId { get; set; }
    public bool StripPrefix { get; set; } = true;
    public bool Enabled { get; set; } = true;

    // How the access token is attached to the forwarded request. Defaults reproduce the only
    // behaviour that existed before these fields — "Authorization: Bearer <token>" — so a store
    // written by an older build deserializes into exactly what it used to do.
    public CredentialPlacement CredentialPlacement { get; set; } = CredentialPlacement.Header;

    /// <summary>Header name, query parameter name, or body field name, per <see cref="CredentialPlacement"/>.</summary>
    public string CredentialParameterName { get; set; } = "Authorization";

    /// <summary>Text placed immediately before the token, e.g. "Bearer " or "token ". May be empty.</summary>
    public string CredentialValuePrefix { get; set; } = "Bearer ";

    public CredentialInjection ToCredentialInjection() =>
        new(CredentialPlacement, CredentialParameterName, CredentialValuePrefix);
}
