using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Storage;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.App.ViewModels;

/// <summary>
/// The banner above the tabs: whether everything is in the vault, and if not, what that means.
///
/// Its whole job is to keep one promise honest. Edits and token refreshes succeed immediately
/// whether or not the password manager is reachable, so without this the UI would show changes as
/// applied while the vault had never heard of them — and exiting would lose them with no warning.
/// </summary>
public sealed partial class VaultStatusViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly VaultSyncQueue _syncQueue;
    private readonly VaultGateService _gate;
    private readonly Dispatcher _dispatcher;

    private readonly CredentialsViewModel _credentials;
    private readonly RoutesViewModel _routes;
    private readonly McpFunnelViewModel _funnels;
    private readonly SettingsViewModel _settings;

    public VaultStatusViewModel(
        ConfigStoreCache configStoreCache,
        VaultSyncQueue syncQueue,
        VaultGateService gate,
        CredentialsViewModel credentials,
        RoutesViewModel routes,
        McpFunnelViewModel funnels,
        SettingsViewModel settings)
    {
        _configStoreCache = configStoreCache;
        _syncQueue = syncQueue;
        _gate = gate;
        _credentials = credentials;
        _routes = routes;
        _funnels = funnels;
        _settings = settings;

        _dispatcher = Dispatcher.CurrentDispatcher;

        // Both fire from thread-pool threads — the sync pump and the refresh loop — so every
        // touch of a bound property has to be marshalled. WPF throws on a cross-thread property
        // change, and it would do it from inside a background service where nothing is watching.
        _syncQueue.StateChanged += _ => _dispatcher.BeginInvoke(Apply);
        _configStoreCache.PendingChanged += () => _dispatcher.BeginInvoke(Apply);

        Apply();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    private bool _hasPendingChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    [NotifyPropertyChangedFor(nameof(IsWaitingForUnlock))]
    private VaultSyncState _state = VaultSyncState.Synced;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";

    /// <summary>The per-manager advice, shown behind "How do I stop this happening?".</summary>
    [ObservableProperty] private string _guidance = "";

    /// <summary>
    /// What the last load changed on its own — a credential dropped because its vault item was
    /// deleted. Its own banner, because it is news about the configuration rather than a state the
    /// user has to act on, and it must not disappear the moment the sync catches up.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    public bool HasNotice => Notice.Length > 0;

    [RelayCommand]
    private void DismissNotice()
    {
        _configStoreCache.DismissLoadNotice();
        Apply();
    }

    /// <summary>
    /// Shown only while something is actually unsaved. A banner that appeared for a syncing state
    /// nobody needs to act on would train the user to ignore it.
    /// </summary>
    public bool IsDegraded => HasPendingChanges && State != VaultSyncState.Syncing;

    public bool IsWaitingForUnlock => State == VaultSyncState.WaitingForUnlock;

    /// <summary>Pushes now, for when the user has just unlocked their manager.</summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        await _syncQueue.FlushAsync(TimeSpan.FromSeconds(30));
        Apply();
    }

    /// <summary>
    /// Discards in-memory state and re-reads the vault — the way out of a secret edited in the
    /// password manager's own UI, which nothing here can be notified about.
    /// </summary>
    [RelayCommand]
    public async Task ReloadFromVaultAsync()
    {
        await _configStoreCache.ReloadAsync();

        ReloadTabs();
        Apply();
    }

    /// <summary>
    /// Loads the store of a password manager that has just been connected again after a
    /// disconnect. Separate from <see cref="ReloadFromVaultAsync"/> because the cache was reset
    /// rather than merely stale, so this is a first load — key backfill and all.
    /// </summary>
    public async Task ReconnectAsync()
    {
        // Off the dispatcher: this is vault I/O, and it can sit on an unlock prompt.
        await Task.Run(() => _configStoreCache.InitializeAsync());

        ReloadTabs();
        Apply();
    }

    private void ReloadTabs()
    {
        _credentials.Reload();
        _routes.Reload();
        _funnels.Reload();
        _settings.Reload();
    }

    private void Apply()
    {
        HasPendingChanges = _configStoreCache.HasPendingChanges;
        State = _syncQueue.State;
        Notice = _configStoreCache.LastLoadNotice ?? "";

        var manager = VaultLockGuidance.DisplayName(_gate.Status.Selected);

        if (!HasPendingChanges)
        {
            Headline = "";
            Detail = "";
            Guidance = "";
            return;
        }

        Guidance = VaultLockGuidance.StayingUnlockedSteps(_gate.Status.Selected);

        Headline = State switch
        {
            VaultSyncState.WaitingForUnlock => $"Waiting for {manager} — your changes are not saved yet.",
            VaultSyncState.Failed => $"{manager} refused the last save.",
            _ => "Saving to your password manager…",
        };

        // The consequence, not just the state. "Not saved" alone reads as a spinner; what the user
        // needs to know is that quitting now throws the changes away.
        Detail = State switch
        {
            VaultSyncState.WaitingForUnlock =>
                $"Unlock {manager} and they will be saved automatically. "
                + "Everything keeps working in the meantime — but if OAuthProxy exits first, these "
                + "changes are lost, and any credential whose token was refreshed will need reconnecting."
                + PendingFor(),

            VaultSyncState.Failed =>
                (_syncQueue.LastError ?? "The vault rejected the change.")
                + " Your changes are still here and will be retried.",

            _ => "",
        };
    }

    private string PendingFor()
    {
        if (_configStoreCache.PendingSince is not { } since) return "";

        var waiting = DateTimeOffset.UtcNow - since;
        if (waiting < TimeSpan.FromMinutes(1)) return "";

        var span = waiting.TotalHours >= 1
            ? $"{(int)waiting.TotalHours} hour(s)"
            : $"{(int)waiting.TotalMinutes} minute(s)";

        return $" Waiting for {span}.";
    }
}
