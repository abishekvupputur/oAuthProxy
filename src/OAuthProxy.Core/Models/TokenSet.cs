namespace OAuthProxy.Core.Models;

public sealed record TokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string TokenType,
    DateTimeOffset ObtainedUtc)
{
    public bool IsExpiringWithin(TimeSpan window) => ExpiresAtUtc - DateTimeOffset.UtcNow < window;
}
