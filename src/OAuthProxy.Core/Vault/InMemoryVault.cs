using System.Text.Json;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Vault;

/// <summary>
/// An <see cref="IConfigVault"/> that keeps the store in memory. Ships in Core rather than the
/// test project because it is what every test that needs a working store uses, and because it is
/// the honest stand-in for "a vault, minus the subprocess".
///
/// Round-trips through JSON on both read and write rather than handing out the same object graph.
/// That is not ceremony: the real backends serialize, so properties marked
/// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> do not survive a save, and a
/// double that shared references would let a test pass on state the real vault would have dropped.
/// It also means a caller cannot mutate "what is stored" by editing the object it saved.
/// </summary>
public sealed class InMemoryVault : IConfigVault
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly VaultAvailability _availability;
    private readonly SaveBehavior _saveBehavior;

    private string? _stored;
    private string? _loadWarning;

    private enum SaveBehavior
    {
        Succeed,
        FailBeforeWriting,
        FailHalfway,
        Locked,
    }

    public InMemoryVault()
        : this(VaultAvailability.Ready, SaveBehavior.Succeed)
    {
    }

    private InMemoryVault(VaultAvailability availability, SaveBehavior saveBehavior)
    {
        _availability = availability;
        _saveBehavior = saveBehavior;
    }

    public VaultBackendKind Kind => VaultBackendKind.None;

    public string? LastLoadWarning { get; private set; }

    /// <summary>A working, initially empty vault.</summary>
    public static InMemoryVault Empty() => new();

    /// <summary>
    /// Fails every save before writing anything. Stands in for a vault that refuses the very first
    /// call — the case where ConfigStoreCache must roll its in-memory state back, because nothing
    /// durable changed.
    /// </summary>
    public static InMemoryVault ThatFailsBeforeWriting() =>
        new(VaultAvailability.Ready, SaveBehavior.FailBeforeWriting);

    /// <summary>
    /// Commits the store and then fails. Stands in for a multi-item save that died between items:
    /// some of it is durable, so ConfigStoreCache must *not* roll back and must instead report
    /// being out of sync.
    /// </summary>
    public static InMemoryVault ThatFailsHalfway() =>
        new(VaultAvailability.Ready, SaveBehavior.FailHalfway);

    /// <summary>
    /// Probes as signed out and refuses writes with <see cref="VaultLockedException"/>. Drives the
    /// read-only mode tests.
    /// </summary>
    public static InMemoryVault ThatIsLocked() =>
        new(VaultAvailability.NotSignedIn, SaveBehavior.Locked);

    /// <summary>Seeds the vault as if the store had already been saved to it.</summary>
    public InMemoryVault Seeded(ConfigStore store)
    {
        _stored = Serialize(store);
        return this;
    }

    /// <summary>
    /// Makes every load report an incomplete result — a real backend does this when a record's
    /// secret item is missing and the record comes back without its secret.
    /// </summary>
    public InMemoryVault WithLoadWarning(string warning)
    {
        _loadWarning = warning;
        return this;
    }

    public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) =>
        Task.FromResult(new VaultStatus(Kind, _availability, VaultId: "in-memory"));

    public Task EnsureVaultAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LastLoadWarning = _loadWarning;

            return _stored is null
                ? new ConfigStore()
                : JsonSerializer.Deserialize<ConfigStore>(_stored, JsonOptions) ?? new ConfigStore();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
    {
        if (_saveBehavior == SaveBehavior.Locked) throw new VaultLockedException(Kind);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_saveBehavior == SaveBehavior.FailBeforeWriting)
            {
                throw new VaultSaveException("Simulated failure before any item was written.",
                    partiallyApplied: false);
            }

            // Serialize before storing so a store that cannot be serialized fails the same way it
            // would against a real backend, rather than being accepted and blowing up on load.
            var serialized = Serialize(store);
            _stored = serialized;

            if (_saveBehavior == SaveBehavior.FailHalfway)
            {
                throw new VaultSaveException("Simulated failure after some items were written.",
                    partiallyApplied: true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string Serialize(ConfigStore store) => JsonSerializer.Serialize(store, JsonOptions);
}
