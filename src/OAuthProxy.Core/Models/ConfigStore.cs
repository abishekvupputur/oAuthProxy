namespace OAuthProxy.Core.Models;

public sealed class ConfigStore
{
    public int SchemaVersion { get; set; } = 1;
    public List<CredentialRecord> Credentials { get; set; } = [];
    public List<UpstreamRecord> Upstreams { get; set; } = [];
    public List<RouteMapping> Routes { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
