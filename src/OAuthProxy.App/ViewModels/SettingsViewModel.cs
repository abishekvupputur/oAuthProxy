using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Models;
using OAuthProxy.Core.Platform;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const int VisibleLogLines = 20;

    private readonly ConfigStoreCache _configStoreCache;
    private readonly AutostartService _autostartService;
    private readonly ActivityLog _activityLog;
    private readonly DispatcherTimer _logTimer;

    [ObservableProperty] private int _listenPort;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _recentActivity = "";
    [ObservableProperty] private string _statusMessage = "Ready.";
    [ObservableProperty] private string _localApiKey = "";
    [ObservableProperty] private bool _isApiKeyVisible;

    /// <summary>Masked unless the user asks to see it, so a shared screen doesn't leak it.</summary>
    public string LocalApiKeyDisplay => IsApiKeyVisible ? LocalApiKey : new string('•', 32);

    public SettingsViewModel(ConfigStoreCache configStoreCache, AutostartService autostartService, ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _autostartService = autostartService;
        _activityLog = activityLog;

        var settings = _configStoreCache.Current.Settings;
        _listenPort = settings.ListenPort;
        _localApiKey = settings.LocalApiKey;

        // The registry is the single source of truth for autostart — it is what Windows
        // actually reads. The persisted Settings.StartWithWindows is kept only so the value
        // survives in the config export; it is never the thing consulted.
        _startWithWindows = _autostartService.IsEnabled();

        RefreshActivity();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _logTimer.Tick += (_, _) => RefreshActivity();
        _logTimer.Start();
    }

    /// <summary>
    /// Re-reads state the tray menu can also change, so the two never disagree. Called when
    /// the Settings tab is shown.
    /// </summary>
    public void Reload()
    {
        var settings = _configStoreCache.Current.Settings;
        ListenPort = settings.ListenPort;
        LocalApiKey = settings.LocalApiKey;
        StartWithWindows = _autostartService.IsEnabled();
    }

    partial void OnIsApiKeyVisibleChanged(bool value) => OnPropertyChanged(nameof(LocalApiKeyDisplay));
    partial void OnLocalApiKeyChanged(string value) => OnPropertyChanged(nameof(LocalApiKeyDisplay));

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
    private void ToggleApiKeyVisibility() => IsApiKeyVisible = !IsApiKeyVisible;

    [RelayCommand]
    private void CopyApiKey()
    {
        try
        {
            System.Windows.Clipboard.SetText(LocalApiKey);
            StatusMessage = "Local API key copied. Send it as the 'X-Proxy-Key' header on every proxied request.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not copy to clipboard: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RegenerateApiKeyAsync()
    {
        var newKey = AppSettings.GenerateApiKey();
        await _configStoreCache.MutateAsync(store => store.Settings.LocalApiKey = newKey);
        LocalApiKey = newKey;
        _activityLog.Log("SETTINGS local API key regenerated — existing clients must be updated");
        StatusMessage = "New key generated. Every client using the old key will now get 403 until updated.";
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
