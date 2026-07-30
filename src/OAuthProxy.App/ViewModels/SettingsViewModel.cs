using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Platform;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const int VisibleLogLines = 20;

    private readonly ConfigStoreCache _configStoreCache;
    private readonly AutostartService _autostartService;
    private readonly ActivityLog _activityLog;
    private readonly VaultGateService _gate;
    private readonly VaultSyncQueue _syncQueue;
    private readonly ProxyConfigChangeNotifier _proxyConfigChangeNotifier;
    private readonly DispatcherTimer _logTimer;

    [ObservableProperty] private int _listenPort;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _recentActivity = "";
    [ObservableProperty] private string _statusMessage = "Ready.";

    /// <summary>Which manager is in use and which vault in it — "Proton Pass — vault 'threeEyedRaven'".</summary>
    [ObservableProperty] private string _passwordManagerSummary = "";

    /// <summary>Where the CLI is and what version answered, so a wrong binary is visible.</summary>
    [ObservableProperty] private string _passwordManagerDetail = "";

    /// <summary>Whether everything in memory has reached the vault, in one line.</summary>
    [ObservableProperty] private string _vaultSyncSummary = "";

    /// <summary>The token option, kept off the lock banner — see <see cref="VaultLockGuidance"/>.</summary>
    [ObservableProperty] private string _unattendedTokenSteps = "";

    [ObservableProperty] private bool _isConnected;

    /// <summary>
    /// Set when disconnecting would throw away changes the vault never got. The button asks once
    /// rather than doing it, because this is the one action in the app that can lose a token
    /// refresh, and there is no undo for it.
    /// </summary>
    [ObservableProperty] private bool _isConfirmingDisconnect;

    /// <summary>
    /// Keys are per endpoint and live on the row that owns them, so this tab only says where to
    /// find them. The counts make an empty install say something useful rather than pointing at
    /// two tabs that have nothing on them yet.
    /// </summary>
    public string KeyLocationSummary
    {
        get
        {
            var store = _configStoreCache.Current;
            var routes = store.Routes.Count;
            var funnels = store.McpFunnels.Count;

            return routes == 0 && funnels == 0
                ? "No endpoints yet. Add a route on the Routes tab (or a funnel on the MCP Funnel tab) "
                  + "and it is issued its own key."
                : $"{routes} route(s) and {funnels} funnel(s), each with its own key. "
                  + "Open the Routes or MCP Funnel tab and use the key controls on the row.";
        }
    }

    public SettingsViewModel(
        ConfigStoreCache configStoreCache,
        AutostartService autostartService,
        ActivityLog activityLog,
        VaultGateService gate,
        VaultSyncQueue syncQueue,
        ProxyConfigChangeNotifier proxyConfigChangeNotifier)
    {
        _configStoreCache = configStoreCache;
        _autostartService = autostartService;
        _activityLog = activityLog;
        _gate = gate;
        _syncQueue = syncQueue;
        _proxyConfigChangeNotifier = proxyConfigChangeNotifier;

        var settings = _configStoreCache.Current.Settings;
        _listenPort = settings.ListenPort;

        // The registry is the single source of truth for autostart — it is what Windows
        // actually reads. The persisted Settings.StartWithWindows is kept only so the value
        // survives in the config export; it is never the thing consulted.
        _startWithWindows = _autostartService.IsEnabled();

        RefreshActivity();
        RefreshVaultStatus();

        // One timer for both: the sync queue and the gate both change state from background
        // threads, and polling them on the dispatcher's own tick avoids marshalling a stream of
        // events into a tab that is usually not even visible.
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _logTimer.Tick += (_, _) =>
        {
            RefreshActivity();
            RefreshVaultStatus();
        };
        _logTimer.Start();
    }

    /// <summary>Raised after the user disconnects, so the shell can go back to the setup page.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Re-reads state the tray menu can also change, so the two never disagree. Called when
    /// the Settings tab is shown.
    /// </summary>
    public void Reload()
    {
        var settings = _configStoreCache.Current.Settings;
        ListenPort = settings.ListenPort;
        StartWithWindows = _autostartService.IsEnabled();

        // Routes and funnels can have been added on another tab since this one was last shown.
        OnPropertyChanged(nameof(KeyLocationSummary));

        RefreshVaultStatus();
    }

    /// <summary>
    /// What the password manager is doing, in the two lines a user actually needs: which vault the
    /// configuration is in, and whether what they see on screen has reached it.
    /// </summary>
    private void RefreshVaultStatus()
    {
        var kind = _gate.Status.Selected;
        var manager = VaultLockGuidance.DisplayName(kind);
        var status = _gate.Status.For(kind);

        IsConnected = kind != VaultBackendKind.None;
        UnattendedTokenSteps = VaultLockGuidance.UnattendedTokenSteps(kind);

        if (!IsConnected)
        {
            PasswordManagerSummary = "Not connected to a password manager.";
            PasswordManagerDetail = "";
            VaultSyncSummary = "";
            return;
        }

        // The vault name is worth stating even when it is the default: once a user has pointed
        // OAuthProxy at a vault of their own, nothing else on screen says which one it went to.
        PasswordManagerSummary = $"{manager} — vault '{status?.VaultName ?? _gate.Selected.VaultName}'";

        PasswordManagerDetail = status?.ExePath is { Length: > 0 } path
            ? status.Version is { Length: > 0 } version ? $"{path}  (v{version})" : path
            : "";

        VaultSyncSummary = DescribeSync(manager);
    }

    private string DescribeSync(string manager)
    {
        if (!_configStoreCache.HasPendingChanges) return $"Everything is saved to {manager}.";

        return _syncQueue.State switch
        {
            VaultSyncState.WaitingForUnlock =>
                $"Waiting for {manager} — changes are in memory only and are lost if OAuthProxy exits first.",

            VaultSyncState.Failed =>
                $"{manager} refused the last save: {_syncQueue.LastError ?? "no reason given"}. Retrying.",

            _ => $"Saving to {manager}…",
        };
    }

    /// <summary>Pushes now, for when the manager has just been unlocked.</summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (!_configStoreCache.HasPendingChanges)
        {
            StatusMessage = "Nothing to save — the vault already has everything.";
            RefreshVaultStatus();
            return;
        }

        StatusMessage = "Saving to your password manager…";

        var saved = await _syncQueue.FlushAsync(TimeSpan.FromSeconds(30));

        StatusMessage = saved
            ? "Saved."
            : _syncQueue.LastError ?? "Could not save — the password manager is locked or unavailable.";

        RefreshVaultStatus();
    }

    /// <summary>
    /// Stops using the password manager and empties the store, which leaves the proxy serving
    /// nothing until one is connected again.
    ///
    /// Tries to save first: the manager is often unlocked by now — the user may have unlocked it
    /// for something else entirely — and discarding changes that could simply have been written
    /// would be a poor way to find that out. Only what is still unsaved after that gets a warning.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_configStoreCache.HasPendingChanges && !IsConfirmingDisconnect)
        {
            StatusMessage = "Saving pending changes before disconnecting…";
            await _syncQueue.FlushAsync(TimeSpan.FromSeconds(15));

            if (_configStoreCache.HasPendingChanges)
            {
                IsConfirmingDisconnect = true;
                StatusMessage = "Some changes have still not reached the vault.";
                return;
            }
        }

        IsConfirmingDisconnect = false;

        _gate.Disconnect();
        await _configStoreCache.ResetAsync();

        // Routes come from the store, so the proxy has to be rebuilt from the now-empty one —
        // otherwise it would keep forwarding with the credentials of a vault this app has just
        // disconnected from.
        _proxyConfigChangeNotifier.Rebuild();

        _activityLog.Log("VAULT disconnected from the Settings tab — the proxy is serving nothing until reconnected");

        StatusMessage = "Disconnected.";
        RefreshVaultStatus();

        Disconnected?.Invoke();
    }

    [RelayCommand]
    private void CancelDisconnect()
    {
        IsConfirmingDisconnect = false;
        StatusMessage = "Left connected.";
    }

    private void RefreshActivity()
    {
        var lines = _activityLog.GetRecent(VisibleLogLines);
        RecentActivity = lines.Count == 0
            ? "(no activity yet)"
            : string.Join(Environment.NewLine, lines);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        // Reload() also assigns this property; skip the write when the registry already agrees,
        // so refreshing the tab doesn't rewrite the Run key.
        if (_autostartService.IsEnabled() == value)
        {
            _configStoreCache.Current.Settings.StartWithWindows = value;
            return;
        }

        if (value) _autostartService.Enable();
        else _autostartService.Disable();

        _ = PersistAutostartAsync(value);
    }

    private async Task PersistAutostartAsync(bool value)
    {
        try
        {
            await _configStoreCache.MutateAsync(store => store.Settings.StartWithWindows = value);
        }
        catch (Exception ex)
        {
            // The registry write already succeeded, so autostart works either way; only the
            // recorded copy of the setting failed. Say so rather than dropping it silently.
            StatusMessage = $"Autostart changed, but saving the setting failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SavePortAsync()
    {
        if (ListenPort is < 1 or > 65535)
        {
            StatusMessage = "Listen port must be between 1 and 65535.";
            return;
        }

        await _configStoreCache.MutateAsync(store => store.Settings.ListenPort = ListenPort);
        StatusMessage = "Saved. Restart OAuthProxy for the new port to take effect.";
    }

    [RelayCommand]
    private void OpenErrorLog()
    {
        if (!File.Exists(_activityLog.ErrorLogPath))
        {
            StatusMessage = "No error log yet — nothing has failed.";
            return;
        }
        OpenInShell(_activityLog.ErrorLogPath);
    }

    [RelayCommand]
    private void OpenActivityLog()
    {
        if (!File.Exists(_activityLog.CurrentLogPath))
        {
            StatusMessage = "No activity log file yet.";
            return;
        }
        OpenInShell(_activityLog.CurrentLogPath);
    }

    [RelayCommand]
    private void OpenLogFolder() => OpenInShell(_activityLog.LogDirectory);

    [RelayCommand]
    private void PruneLogs()
    {
        var deleted = _activityLog.PruneAll();
        StatusMessage = deleted == 0
            ? "Nothing to prune — only the current log exists."
            : $"Pruned {deleted} log file(s). The current activity log was kept.";
    }

    private void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open '{path}': {ex.Message}";
        }
    }
}
