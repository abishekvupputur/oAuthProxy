using OAuthProxy.Core.Models;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.Core.Storage;

/// <summary>
/// In-memory source of truth for the app's config, loaded once at host startup. UI, the
/// proxy transform, and the refresh loop all read/write through this instead of reaching for
/// the vault directly.
///
/// Reads are free and writes are not: the vault is a subprocess and a network round trip, so
/// everything on the request path reads <see cref="Current"/> and only deliberate edits and
/// token refreshes go through <see cref="MutateAsync"/>/<see cref="SaveAsync"/>.
/// </summary>
public sealed class ConfigStoreCache
{
    private readonly IConfigVault _vault;
    private ConfigStore _current = new();
    private bool _initialized;

    /// <summary>
    /// Held across the whole serialize-and-write, and by callers while they mutate the store.
    /// Three threads touch this object — the WPF dispatcher (UI edits), the token refresh
    /// loop, and thread-pool threads serving proxied requests. Without this, a UI-thread
    /// Credentials.Add() landing mid-serialization throws "Collection was modified" out of
    /// SaveAsync, which used to escape the refresh loop and stop the entire host.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigStoreCache(IConfigVault vault)
    {
        _vault = vault;
    }

    public ConfigStore Current => _current;

    public bool IsInitialized => _initialized;

    /// <summary>
    /// Loads the store and issues any missing endpoint keys. Idempotent: startup calls this
    /// directly (it needs the listen port before Kestrel can bind) and
    /// <see cref="ConfigStoreInitializerHostedService"/> calls it again as the host starts, which
    /// must not re-read the vault and discard edits made in between.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var loaded = await _vault.LoadAsync(ct);
        _current = loaded;
        _initialized = true;

        // Every route and funnel needs its own key. This covers a store written before
        // per-endpoint keys existed (where the single Settings.LocalApiKey was the only secret,
        // and is now ignored) as well as any record that somehow reached disk without one.
        //
        // The generated values must be persisted here and nowhere else — if they only ever lived
        // in memory, every restart would produce different keys and every client would break at
        // random.
        if (BackfillKeys(loaded) > 0)
        {
            await SaveAsync(ct);
        }
    }

    /// <summary>
    /// Issues a never-expiring key to every route and funnel that has none. Returns how many were
    /// issued, so the caller can skip the write when there is nothing to do.
    /// </summary>
    private static int BackfillKeys(ConfigStore store)
    {
        var issued = 0;

        foreach (var key in store.Routes.Select(r => r.Key).Concat(store.McpFunnels.Select(f => f.Key)))
        {
            if (key.IsConfigured) continue;

            var fresh = ProxyKey.Generate();
            key.Value = fresh.Value;
            key.CreatedUtc = fresh.CreatedUtc;
            key.ExpiresUtc = null;
            issued++;
        }

        return issued;
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
            await _vault.SaveAsync(_current, ct).ConfigureAwait(false);
            IsOutOfSync = false;
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
    /// If the write fails without reaching the vault the mutation is undone, so memory never
    /// claims something the vault does not have. Previously a failed save left the edit applied in
    /// memory: the UI showed the new credential, the proxy routed to it, and it vanished at the
    /// next restart — silent data loss the user only discovered hours later.
    ///
    /// A save that failed *part way* is the one case where rolling back is wrong. Saving the store
    /// is several vault items, so a mid-save failure leaves some of them durable; reverting memory
    /// would make the next successful save delete records that are already safely stored. Memory
    /// stays as the newest truth, <see cref="IsOutOfSync"/> is raised, and the user is offered a
    /// retry or a reload rather than having the choice made for them.
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
                await _vault.SaveAsync(_current, ct).ConfigureAwait(false);
                IsOutOfSync = false;
            }
            catch (VaultSaveException ex) when (ex.PartiallyApplied)
            {
                IsOutOfSync = true;
                throw;
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
    /// True when a save reached the vault and failed part way, so some records are stored and some
    /// are not. Cleared by the next successful save or by <see cref="ReloadAsync"/>.
    /// </summary>
    public bool IsOutOfSync { get; private set; }

    /// <summary>
    /// Discards the in-memory store and re-reads the vault. The escape hatch for the two cases the
    /// app cannot resolve on its own: a half-applied save, and a secret edited in the password
    /// manager's own UI, which nothing here can be notified about.
    ///
    /// Callers must reload their view models afterwards — this replaces the contents of the
    /// existing <see cref="ConfigStore"/> lists, so bindings to the store itself survive, but
    /// bindings to individual records do not.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var loaded = await _vault.LoadAsync(ct).ConfigureAwait(false);
            RestoreInto(_current, Snapshot(loaded));
            IsOutOfSync = false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Membership of every list plus the settings scalars — which is exactly what callers
    /// change through <see cref="MutateAsync"/> (add/remove a credential, upstream, route, MCP
    /// source, or funnel; set a port or key).
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
        List<McpSourceRecord> McpSources,
        List<McpFunnelRecord> McpFunnels,
        int ListenPort,
        bool StartWithWindows,
        bool McpFunnelEnabled);

    private static StoreSnapshot Snapshot(ConfigStore store) => new(
        [.. store.Credentials],
        [.. store.Upstreams],
        [.. store.Routes],
        [.. store.McpSources],
        [.. store.McpFunnels],
        store.Settings.ListenPort,
        store.Settings.StartWithWindows,
        store.Settings.McpFunnelEnabled);

    private static void RestoreInto(ConfigStore store, StoreSnapshot snapshot)
    {
        ReplaceAll(store.Credentials, snapshot.Credentials);
        ReplaceAll(store.Upstreams, snapshot.Upstreams);
        ReplaceAll(store.Routes, snapshot.Routes);
        ReplaceAll(store.McpSources, snapshot.McpSources);
        ReplaceAll(store.McpFunnels, snapshot.McpFunnels);

        store.Settings.ListenPort = snapshot.ListenPort;
        store.Settings.StartWithWindows = snapshot.StartWithWindows;
        store.Settings.McpFunnelEnabled = snapshot.McpFunnelEnabled;
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
