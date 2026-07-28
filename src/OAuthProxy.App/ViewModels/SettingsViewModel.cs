using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Diagnostics;
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

    public SettingsViewModel(ConfigStoreCache configStoreCache, AutostartService autostartService, ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _autostartService = autostartService;
        _activityLog = activityLog;

        var settings = _configStoreCache.Current.Settings;
        _listenPort = settings.ListenPort;
        _startWithWindows = _autostartService.IsEnabled();

        RefreshActivity();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _logTimer.Tick += (_, _) => RefreshActivity();
        _logTimer.Start();
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
        if (value) _autostartService.Enable();
        else _autostartService.Disable();

        _configStoreCache.Current.Settings.StartWithWindows = value;
        _ = _configStoreCache.SaveAsync();
    }

    [RelayCommand]
    private async Task SavePortAsync()
    {
        _configStoreCache.Current.Settings.ListenPort = ListenPort;
        await _configStoreCache.SaveAsync();
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
