using Google.Apis.Util.Store;

namespace OAuthProxy.Core.Auth;

/// <summary>
/// Google.Apis.Auth writes tokens to a FileDataStore by default. We own persistence
/// (SecureStore's DPAPI-encrypted store) so this store is a deliberate no-op — nothing
/// gets written to a second, unencrypted location on disk.
/// </summary>
internal sealed class NoOpDataStore : IDataStore
{
    public Task StoreAsync<T>(string key, T value) => Task.CompletedTask;

    public Task DeleteAsync<T>(string key) => Task.CompletedTask;

    public Task<T> GetAsync<T>(string key) => Task.FromResult(default(T)!);

    public Task ClearAsync() => Task.CompletedTask;
}
