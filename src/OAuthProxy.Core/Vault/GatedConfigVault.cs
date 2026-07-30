using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Vault;

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

    public string? LastLoadWarning => Current.LastLoadWarning;

    public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) => Current.ProbeAsync(ct);

    public Task EnsureVaultAsync(CancellationToken ct = default) => Current.EnsureVaultAsync(ct);

    public Task<ConfigStore> LoadAsync(CancellationToken ct = default) => Current.LoadAsync(ct);

    public Task SaveAsync(ConfigStore store, CancellationToken ct = default) => Current.SaveAsync(store, ct);
}
