using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// The <see cref="IConfigVault"/> everything else resolves, forwarding to whichever backend the
/// gate has settled on.
///
/// Needed because the two ends disagree about timing: ConfigStoreCache is a singleton built during
/// host construction, while the backend is not known until the gate has probed both managers and
/// possibly asked the user. Without this indirection the cache would capture the placeholder vault
/// at construction and keep writing to it forever — the app would look like it was saving and
/// nothing would reach the password manager.
/// </summary>
public sealed class GatedConfigVault(VaultGateService gate) : IConfigVault
{
    private IConfigVault Current => gate.Selected;

    public VaultBackendKind Kind => Current.Kind;

    public string VaultName => Current.VaultName;

    public string? LastLoadWarning => Current.LastLoadWarning;

    public IReadOnlyList<string> LastLoadRemovals => Current.LastLoadRemovals;

    public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) => Current.ProbeAsync(ct);

    public Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default) =>
        Current.ProbeAsync(depth, ct);

    public Task CreateVaultAsync(string vaultName, CancellationToken ct = default) =>
        Current.CreateVaultAsync(vaultName, ct);

    public Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default) =>
        Current.UseExistingVaultAsync(vaultName, ct);

    public void Forget() => Current.Forget();

    public Task<ConfigStore> LoadAsync(CancellationToken ct = default) => Current.LoadAsync(ct);

    public Task SaveAsync(ConfigStore store, CancellationToken ct = default) => Current.SaveAsync(store, ct);

    public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) =>
        Current.RewriteAllAsync(store, ct);

    public Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default) =>
        Current.ListLiveItemsAsync(ct);

    public Task DeleteItemAsync(string itemId, CancellationToken ct = default) =>
        Current.DeleteItemAsync(itemId, ct);
}
