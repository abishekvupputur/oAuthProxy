using System.Security.Cryptography;
using System.Text.Json;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Storage;

/// <summary>
/// Whole-file DPAPI encryption of the ConfigStore JSON. Purely a disk (de)serialization
/// layer — ConfigStoreCache is the in-memory source of truth callers actually read/write.
/// </summary>
public sealed class SecureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public SecureStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OAuthProxy",
            "store.dat");
    }

    public async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
                return new ConfigStore();

            var protectedBytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<ConfigStore>(jsonBytes, JsonOptions) ?? new ConfigStore();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(store, JsonOptions);
            var protectedBytes = ProtectedData.Protect(jsonBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);

            var tempPath = _filePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, protectedBytes, ct).ConfigureAwait(false);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
