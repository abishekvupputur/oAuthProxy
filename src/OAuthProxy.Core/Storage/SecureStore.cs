using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Storage;

/// <summary>
/// Whole-file DPAPI encryption of the ConfigStore JSON. Purely a disk (de)serialization
/// layer — ConfigStoreCache is the in-memory source of truth callers actually read/write.
///
/// Threat model: DataProtectionScope.CurrentUser means anything running as this user can
/// decrypt the file. That is the ceiling for a desktop app with no master password, and
/// adding entropy would not raise it (the entropy would have to live in the binary). What
/// this does buy is protection against the file being read from a backup, another account,
/// or another machine.
/// </summary>
public sealed class SecureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>Set when the last load found an unreadable file that was quarantined.</summary>
    public string? QuarantinedFilePath { get; private set; }

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

            try
            {
                var protectedBytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
                var jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                var store = JsonSerializer.Deserialize<ConfigStore>(jsonBytes, JsonOptions) ?? new ConfigStore();

                // A route used to hold exactly one credential in four scalar fields. Folding
                // those into the credential list here, once, is what lets everything downstream
                // read one shape — and clears the old fields so the next save stops writing two
                // representations of the same thing.
                foreach (var route in store.Routes) route.Normalize();

                return store;
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
            {
                // Three real ways to get here: a half-written file from a hard power loss, a
                // profile copied to another machine or user (DPAPI cannot unprotect it), and a
                // schema the current build cannot parse. Previously any of them threw out of
                // app startup, which the dispatcher handler swallowed — leaving a running
                // process with no tray icon, no window, and no explanation. Quarantine the
                // file and come up empty instead, so the app is at least usable and the old
                // data is still on disk if it can be recovered.
                QuarantinedFilePath = Quarantine();
                return new ConfigStore();
            }
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
            try
            {
                await File.WriteAllBytesAsync(tempPath, protectedBytes, ct).ConfigureAwait(false);
                RestrictToCurrentUser(tempPath);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
                // Without this the temp file survives every failed save and accumulates. It is
                // encrypted, so this is tidiness rather than a leak, but a stale .tmp next to
                // the real store is also confusing to anyone inspecting the folder.
                TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Renames an unreadable store aside rather than deleting it — the bytes may still be
    /// recoverable (e.g. by the original user account) and silently destroying someone's
    /// credential set is worse than leaving a file behind.
    /// </summary>
    private string? Quarantine()
    {
        try
        {
            var quarantinePath = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_filePath, quarantinePath, overwrite: true);
            return quarantinePath;
        }
        catch
        {
            // If even the rename fails there is nothing useful left to do; starting empty is
            // still better than refusing to start.
            return null;
        }
    }

    /// <summary>
    /// %APPDATA% is already user-scoped, so this is defense in depth for a profile with
    /// loosened inherited permissions. Best-effort: a failure here must not block the save,
    /// since DPAPI is still doing the actual protecting.
    /// </summary>
    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;

            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null) return;

            security.SetOwner(currentUser);
            security.SetAccessRule(new FileSystemAccessRule(
                currentUser, FileSystemRights.FullControl, AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
        }
        catch
        {
            // ignored — DPAPI remains the real protection
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignored
        }
    }
}
