using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Storage;

/// <summary>
/// In-memory source of truth for the app's config, loaded once at host startup. UI, the
/// proxy transform, and the refresh loop all read/write through this instead of touching
/// disk directly.
/// </summary>
public sealed class ConfigStoreCache
{
    private readonly SecureStore _secureStore;
    private ConfigStore _current = new();
    private readonly object _swapLock = new();

    public ConfigStoreCache(SecureStore secureStore)
    {
        _secureStore = secureStore;
    }

    public ConfigStore Current => _current;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _current = await _secureStore.LoadAsync(ct);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _secureStore.SaveAsync(_current, ct);
    }

    public CredentialRecord? GetCredential(Guid id) =>
        _current.Credentials.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Atomically swaps in a new TokenSet for a credential. TokenSet is immutable, so a
    /// proxy request reading credential.Token concurrently sees either the fully-old or
    /// fully-new value, never a torn one.
    /// </summary>
    public void ReplaceCredentialToken(Guid credentialId, TokenSet newToken)
    {
        lock (_swapLock)
        {
            var credential = _current.Credentials.FirstOrDefault(c => c.Id == credentialId);
            if (credential is null) return;
            credential.Token = newToken;
            credential.NeedsReconnect = false;
        }
    }
}
