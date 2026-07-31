namespace RavensPort.Core.Models;

public sealed class UpstreamRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string BaseUrl { get; set; }
}
