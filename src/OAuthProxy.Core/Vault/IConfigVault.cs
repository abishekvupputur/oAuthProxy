using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Vault;

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
    /// Checks whether the backend is installed, signed in, and holding the vault. Cheap enough to
    /// call on a timer — implementations must not fetch item contents here.
    /// </summary>
    Task<VaultStatus> ProbeAsync(CancellationToken ct = default);

    /// <summary>Creates the vault if it does not exist. No-op when it already does.</summary>
    Task EnsureVaultAsync(CancellationToken ct = default);

    Task<ConfigStore> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the whole store. Throws <see cref="VaultSaveException"/> on failure, with
    /// PartiallyApplied set when some items were already written.
    /// </summary>
    Task SaveAsync(ConfigStore store, CancellationToken ct = default);

    /// <summary>
    /// Set when the last load succeeded but came back incomplete — typically a record whose secret
    /// item is missing, so it loads without its client secret or key. Null when the last load was
    /// clean. Surfaced at startup rather than swallowed, because the alternative is a credential
    /// that silently stops working with nothing anywhere to explain it.
    /// </summary>
    string? LastLoadWarning { get; }
}
