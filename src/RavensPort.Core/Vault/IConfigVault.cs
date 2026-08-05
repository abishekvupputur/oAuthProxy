using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// The store. Everything the app knows — credentials, tokens, API keys, per-endpoint proxy keys,
/// routes, MCP sources and funnels, settings — lives behind this and nowhere else. There is no
/// local file, no cache, and no fallback: if the vault cannot be reached the app has no config.
///
/// <see cref="LoadAsync"/> and <see cref="SaveAsync"/> take and return the whole
/// <see cref="ConfigStore"/> rather than exposing per-record operations. The store is small, the
/// UI edits it as one object graph, and a whole-document contract is what makes the write path
/// atomic enough to reason about — implementations map it onto individual vault items internally.
/// </summary>
public interface IConfigVault
{
    VaultBackendKind Kind { get; }

    /// <summary>
    /// The vault being read and written — <see cref="VaultConstants.VaultName"/> unless the user
    /// pointed this backend at a vault they already had.
    /// </summary>
    string VaultName { get; }

    /// <summary>
    /// Checks whether the backend is installed, signed in, and holding the vault. Cheap enough to
    /// call on a timer — implementations must not fetch item contents here.
    /// </summary>
    Task<VaultStatus> ProbeAsync(CancellationToken ct = default);

    /// <summary>
    /// The same check, bounded by how much the caller is allowed to disturb the user.
    ///
    /// A <see cref="VaultProbeDepth.Discovery"/> probe must not run anything that can raise an
    /// unlock prompt: locating the binary and asking its version is the whole budget, and the
    /// answer for an installed manager is <see cref="VaultAvailability.NotConnected"/>. That is
    /// what lets the app start without demanding a gesture for each password manager the machine
    /// happens to have — see <see cref="VaultProbeDepth"/>.
    /// </summary>
    Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default);

    /// <summary>
    /// Creates a vault with the given name and starts using it.
    ///
    /// The name is the user's rather than fixed: RavensPort is only the default this app looks
    /// for first, and someone keeping separate profiles needs to say which one they are making. The
    /// new vault is stamped with the config item on the way out, because that item is what
    /// identifies it on the next launch — nothing about this app is stored on the PC.
    /// </summary>
    Task CreateVaultAsync(string vaultName, CancellationToken ct = default);

    /// <summary>
    /// Uses a vault the user already has, instead of creating RavensPort. Accepts only a vault
    /// RavensPort has written to before or one that is completely empty, and throws
    /// <see cref="VaultAdoptionException"/> with the reason otherwise — see <see cref="VaultAdoption"/>
    /// for why anything else is refused. An empty one is stamped with the config item on the way in,
    /// since that item is what identifies the vault again on the next launch.
    /// </summary>
    Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default);

    /// <summary>
    /// Drops everything learned about this backend — the vault it resolved and the revision it
    /// loaded. For disconnecting: without it, reconnecting would keep writing to the vault the user
    /// just walked away from.
    /// </summary>
    void Forget();

    Task<ConfigStore> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the whole store. Throws <see cref="VaultSaveException"/> on failure, with
    /// PartiallyApplied set when some items were already written.
    /// </summary>
    Task SaveAsync(ConfigStore store, CancellationToken ct = default);

    /// <summary>
    /// Writes every item and the config note again, whether or not anything changed.
    ///
    /// A normal save skips items whose secret has not moved, which is what keeps a port change to
    /// one CLI call. That optimisation is also what leaves a vault edited by hand out of step with
    /// the app, so this is the way back: what is in memory becomes what is in the vault.
    /// </summary>
    Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default);

    /// <summary>
    /// Every live item in the vault, for the integrity check — this app's and the user's alike,
    /// each flagged with which it is. Titles only: no secrets are fetched, and items the password
    /// manager considers deleted are not returned.
    ///
    /// Deliberately unfiltered. Saving looks only at items titled as this app's, which is what
    /// keeps the user's entries out of reach of delete reconciliation — but it also means an item
    /// of ours whose title was edited becomes invisible to every save. Looking has to see more
    /// than saving does, or that item can never be reported.
    /// </summary>
    Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes one item the user has asked to be rid of. Unlike the tolerant delete inside a save,
    /// this throws when it fails: it is the whole point of the action rather than housekeeping.
    /// </summary>
    Task DeleteItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Set when the last load succeeded but came back incomplete — typically a record whose secret
    /// item is missing, so it loads without its client secret or key. Null when the last load was
    /// clean. Surfaced at startup rather than swallowed, because the alternative is a credential
    /// that silently stops working with nothing anywhere to explain it.
    /// </summary>
    string? LastLoadWarning { get; }

    /// <summary>
    /// What the last load dropped to match the vault — a credential whose item was deleted in the
    /// password manager's own UI, say. Empty when the load changed nothing.
    ///
    /// Non-empty means the store no longer matches the note it came from, so the caller must write
    /// it back. Without that, the note keeps its record of a credential that no longer exists and
    /// every launch resurrects the same ghost.
    /// </summary>
    IReadOnlyList<string> LastLoadRemovals { get; }
}
