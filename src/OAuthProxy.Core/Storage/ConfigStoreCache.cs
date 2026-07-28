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

    /// <summary>
    /// Held across the whole serialize-and-write, and by callers while they mutate the store.
    /// Three threads touch this object — the WPF dispatcher (UI edits), the token refresh
    /// loop, and thread-pool threads serving proxied requests. Without this, a UI-thread
    /// Credentials.Add() landing mid-serialization throws "Collection was modified" out of
    /// SaveAsync, which used to escape the refresh loop and stop the entire host.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigStoreCache(SecureStore secureStore)
    {
        _secureStore = secureStore;
    }

    public ConfigStore Current => _current;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loaded = await _secureStore.LoadAsync(ct);

        // Covers both a brand-new install and a store written before this key existed. The
        // generated value must be persisted here and nowhere else — if it only ever lived in
        // memory, every restart would produce a different key and clients would break at
        // random.
        if (string.IsNullOrEmpty(loaded.Settings.LocalApiKey))
        {
            loaded.Settings.LocalApiKey = AppSettings.GenerateApiKey();
            _current = loaded;
            await SaveAsync(ct);
            return;
        }

        _current = loaded;
    }

    /// <summary>
    /// Persists the current store. Safe to call concurrently — writes are serialized, and the
    /// snapshot is taken under the same lock callers use via <see cref="MutateAsync"/>.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _secureStore.SaveAsync(_current, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Applies a mutation and persists it as one atomic unit. Every write path should use this
    /// rather than mutating <see cref="Current"/> and then calling <see cref="SaveAsync"/>
    /// separately — that leaves a window where another thread serializes a half-applied edit.
    ///
    /// If the write fails the mutation is undone, so memory never claims something disk does
    /// not have. Previously a failed save (full disk, file locked by antivirus) left the edit
    /// applied in memory: the UI showed the new credential, the proxy routed to it, and it
    /// vanished at the next restart — silent data loss the user only discovered hours later.
    /// </summary>
    public async Task MutateAsync(Action<ConfigStore> mutate, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rollback = Snapshot(_current);
            mutate(_current);

            try
            {
                await _secureStore.SaveAsync(_current, ct).ConfigureAwait(false);
            }
            catch
            {
                RestoreInto(_current, rollback);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Membership of the three lists plus the settings scalars — which is exactly what callers
    /// change through <see cref="MutateAsync"/> (add/remove a credential, upstream, or route;
    /// set a port or key).
    ///
    /// Deliberately shallow, and restored *into* the existing instances rather than by swapping
    /// <see cref="Current"/> for a clone. The view models hold direct references to individual
    /// <see cref="CredentialRecord"/>/<see cref="RouteMapping"/> objects and to the store
    /// itself; handing back a fresh object graph would leave every one of those bindings
    /// pointing at an orphan, so a rollback meant to protect data would quietly detach the UI
    /// from it. The trade-off: an in-place field edit of a record that was already in the store
    /// is not undone — that reverts on next load instead, which is visible and recoverable,
    /// unlike a vanished credential.
    /// </summary>
    private sealed record StoreSnapshot(
        List<CredentialRecord> Credentials,
        List<UpstreamRecord> Upstreams,
        List<RouteMapping> Routes,
        int ListenPort,
        bool StartWithWindows,
        string LocalApiKey);

    private static StoreSnapshot Snapshot(ConfigStore store) => new(
        [.. store.Credentials],
        [.. store.Upstreams],
        [.. store.Routes],
        store.Settings.ListenPort,
        store.Settings.StartWithWindows,
        store.Settings.LocalApiKey);

    private static void RestoreInto(ConfigStore store, StoreSnapshot snapshot)
    {
        ReplaceAll(store.Credentials, snapshot.Credentials);
        ReplaceAll(store.Upstreams, snapshot.Upstreams);
        ReplaceAll(store.Routes, snapshot.Routes);

        store.Settings.ListenPort = snapshot.ListenPort;
        store.Settings.StartWithWindows = snapshot.StartWithWindows;
        store.Settings.LocalApiKey = snapshot.LocalApiKey;
    }

    private static void ReplaceAll<T>(List<T> target, List<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }

    /// <summary>
    /// Waits for any in-flight write to finish. Some UI toggles start a save without awaiting
    /// it (a checkbox setter is synchronous), and shutdown ends in Environment.Exit — which
    /// would otherwise kill the process mid-write and silently drop the change.
    /// </summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
            _writeLock.Release();
        }
        catch (OperationCanceledException)
        {
            // A stuck write must not be able to block exit.
        }
    }

    public CredentialRecord? GetCredential(Guid id) =>
        _current.Credentials.FirstOrDefault(c => c.Id == id);
}
