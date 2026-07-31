using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>What the app knows about both backends right now.</summary>
/// <param name="Statuses">One entry per supported manager, in a stable order for the setup page.</param>
/// <param name="Selected">The chosen backend, once there is one.</param>
/// <param name="NeedsAChoice">
/// True when more than one manager qualifies and nothing in the vaults says which was meant. The
/// backend choice is the one piece of state that cannot live in the vault, and it is deliberately
/// not stored anywhere else — so this asks, every launch, rather than remembering.
/// </param>
public sealed record VaultGateStatus(
    IReadOnlyList<VaultStatus> Statuses,
    VaultBackendKind Selected,
    bool NeedsAChoice)
{
    public bool IsReady => Selected != VaultBackendKind.None && !NeedsAChoice;

    public VaultStatus? For(VaultBackendKind kind) => Statuses.FirstOrDefault(s => s.Kind == kind);
}

/// <summary>
/// Decides which password manager backs the store, and whether the app can start at all.
///
/// Resolution is by discovery rather than by remembered preference: whichever manager's
/// RavensPort vault already holds a RavensPort configuration <em>is</em> the backend. That answers
/// the normal case with no stored state, which matters because there is nowhere to store it — the
/// whole design is that nothing about this app persists outside the vault. Only a genuine tie asks.
/// </summary>
public sealed class VaultGateService
{
    private readonly OnePasswordVaultProvider _onePassword;
    private readonly ProtonPassVaultProvider _protonPass;
    private readonly ActivityLog _activityLog;

    public VaultGateService(
        OnePasswordVaultProvider onePassword,
        ProtonPassVaultProvider protonPass,
        ActivityLog activityLog)
    {
        _onePassword = onePassword;
        _protonPass = protonPass;
        _activityLog = activityLog;

        Status = new VaultGateStatus([], VaultBackendKind.None, NeedsAChoice: false);
    }

    /// <summary>
    /// Set by <see cref="Disconnect"/> until the user picks a manager again.
    ///
    /// Without it, disconnecting would be undone by the very next probe: a single ready manager
    /// resolves itself with no question asked, which is right at startup and wrong immediately
    /// after someone has said they want to stop using it.
    /// </summary>
    private bool _disconnected;

    public VaultGateStatus Status { get; private set; }

    /// <summary>True after a disconnect, until a backend is chosen again.</summary>
    public bool IsDisconnected => _disconnected;

    /// <summary>The active backend. Never null so callers do not have to special-case startup.</summary>
    public IConfigVault Selected { get; private set; } = new InMemoryVault();

    public event Action<VaultGateStatus>? StatusChanged;

    /// <summary>
    /// Probes both managers and resolves a backend if it can. Safe to call repeatedly — the setup
    /// page's "Check again" is exactly this.
    /// </summary>
    public async Task<VaultGateStatus> EvaluateAsync(CancellationToken ct = default)
    {
        // Concurrently: each is a subprocess launch that may sit on an unlock prompt, and running
        // them in sequence would double the worst case on the startup path.
        var probes = await Task.WhenAll(
            ProbeSafelyAsync(_onePassword, ct),
            ProbeSafelyAsync(_protonPass, ct));

        var ready = probes.Where(p => p.IsReady).ToList();

        // Disconnected means the answer to "which backend" is nobody, no matter what the probes
        // found. NeedsAChoice so the setup page offers the ready ones as buttons to connect back.
        if (_disconnected)
        {
            return Publish(new VaultGateStatus(probes, VaultBackendKind.None, NeedsAChoice: ready.Count > 0));
        }

        return ready.Count switch
        {
            0 => Publish(new VaultGateStatus(probes, VaultBackendKind.None, NeedsAChoice: false)),
            1 => Publish(new VaultGateStatus(probes, ready[0].Kind, NeedsAChoice: false), ready[0].Kind),
            _ => await ResolveTieAsync(probes, ready, ct),
        };
    }

    /// <summary>
    /// Both managers hold the vault. Whichever one already has a configuration in it is the one
    /// that was being used; only if that is ambiguous does the user get asked.
    /// </summary>
    private async Task<VaultGateStatus> ResolveTieAsync(
        VaultStatus[] probes, List<VaultStatus> ready, CancellationToken ct)
    {
        var configured = new List<VaultBackendKind>();

        foreach (var status in ready)
        {
            if (await HasConfigurationAsync(ProviderFor(status.Kind), ct)) configured.Add(status.Kind);
        }

        if (configured.Count == 1)
        {
            _activityLog.Log($"STARTUP both password managers are available; using "
                             + $"{VaultLockGuidance.DisplayName(configured[0])}, which holds the configuration");

            return Publish(new VaultGateStatus(probes, configured[0], NeedsAChoice: false), configured[0]);
        }

        // Either both hold a configuration or neither does. Guessing would mean silently reading
        // one and silently overwriting the other, so this is a question only the user can answer.
        return Publish(new VaultGateStatus(probes, VaultBackendKind.None, NeedsAChoice: true));
    }

    /// <summary>Records the user's answer for this run. Deliberately not persisted anywhere.</summary>
    public VaultGateStatus SelectBackend(VaultBackendKind kind)
    {
        _disconnected = false;

        var status = Status with { Selected = kind, NeedsAChoice = false };
        return Publish(status, kind);
    }

    /// <summary>Creates a vault with the user's chosen name in the given manager, then re-evaluates.</summary>
    public async Task<VaultGateStatus> CreateVaultAsync(
        VaultBackendKind kind, string vaultName, CancellationToken ct = default)
    {
        _disconnected = false;

        await ProviderFor(kind).CreateVaultAsync(vaultName, ct);
        _activityLog.Log($"STARTUP created the '{ProviderFor(kind).VaultName}' vault in {VaultLockGuidance.DisplayName(kind)}");

        return await ResolveAfterUserChoiceAsync(kind, ct);
    }

    /// <summary>
    /// Points a manager at a vault the user already has, then re-evaluates. Throws
    /// <see cref="VaultAdoptionException"/> when that vault may not be used.
    /// </summary>
    public async Task<VaultGateStatus> UseExistingVaultAsync(
        VaultBackendKind kind, string vaultName, CancellationToken ct = default)
    {
        _disconnected = false;

        await ProviderFor(kind).UseExistingVaultAsync(vaultName, ct);

        return await ResolveAfterUserChoiceAsync(kind, ct);
    }

    /// <summary>
    /// Stops using the password manager: both providers forget the vault they resolved, and the
    /// app is back where it starts, holding nothing.
    ///
    /// The caller is expected to clear the in-memory store as well. Leaving it loaded would be the
    /// worst of both worlds — the proxy would still be spending the user's tokens using a
    /// configuration they have just disconnected from, and with nowhere to save changes to.
    /// </summary>
    public VaultGateStatus Disconnect()
    {
        _onePassword.Forget();
        _protonPass.Forget();

        _disconnected = true;
        Selected = new InMemoryVault();

        _activityLog.Log("VAULT disconnected from the password manager — RavensPort is holding no configuration");

        return Publish(new VaultGateStatus([], VaultBackendKind.None, NeedsAChoice: false));
    }

    /// <summary>
    /// Re-probes after the user has said which manager to use, and keeps that answer. Without the
    /// explicit select, a tie-break could hand the app to the <em>other</em> manager immediately
    /// after someone created or named a vault in this one.
    /// </summary>
    private async Task<VaultGateStatus> ResolveAfterUserChoiceAsync(VaultBackendKind kind, CancellationToken ct)
    {
        var status = await EvaluateAsync(ct);

        return status.For(kind)?.IsReady == true ? SelectBackend(kind) : status;
    }

    public IConfigVault ProviderFor(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword => _onePassword,
        VaultBackendKind.ProtonPass => _protonPass,
        _ => Selected,
    };

    /// <summary>
    /// Whether this manager's vault already holds a RavensPort configuration. A load rather than a
    /// guess, because "there is a vault" and "there is anything in it" are different questions and
    /// only the second one identifies which manager was being used.
    /// </summary>
    private async Task<bool> HasConfigurationAsync(IConfigVault vault, CancellationToken ct)
    {
        try
        {
            var store = await vault.LoadAsync(ct);

            return store.Credentials.Count > 0
                   || store.Routes.Count > 0
                   || store.Upstreams.Count > 0
                   || store.McpFunnels.Count > 0;
        }
        catch (Exception ex) when (ex is VaultCliException or VaultLockedException)
        {
            // Unreadable is not the same as empty, but for choosing between two managers it leads
            // to the same place: this one cannot be shown to hold the configuration.
            _activityLog.Log($"STARTUP could not read {VaultLockGuidance.DisplayName(vault.Kind)}: {ex.Message}");
            return false;
        }
    }

    private static async Task<VaultStatus> ProbeSafelyAsync(IConfigVault vault, CancellationToken ct)
    {
        try
        {
            return await vault.ProbeAsync(ct);
        }
        catch (Exception ex)
        {
            // A probe must never be able to stop the app reaching its setup page — that page is
            // the only thing that can explain what went wrong.
            return VaultStatus.Faulted(vault.Kind, ex.Message);
        }
    }

    private VaultGateStatus Publish(VaultGateStatus status, VaultBackendKind? selected = null)
    {
        Status = status;

        if (selected is { } kind && kind != VaultBackendKind.None) Selected = ProviderFor(kind);

        StatusChanged?.Invoke(status);
        return status;
    }
}
