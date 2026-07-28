namespace OAuthProxy.Core.Models;

public sealed class RouteMapping
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string PathPrefix { get; set; }
    public Guid UpstreamId { get; set; }
    public Guid CredentialId { get; set; }
    public bool StripPrefix { get; set; } = true;
    public bool Enabled { get; set; } = true;
}
