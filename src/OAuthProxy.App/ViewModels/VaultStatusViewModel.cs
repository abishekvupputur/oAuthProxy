using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Storage;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.App.ViewModels;

/// <summary>
/// Turns the store's read-only state into something the window can show, and pushes it into every
/// view model that owns a command capable of writing.
///
/// Core stays MVVM-free — it raises a plain event — so the translation into observable properties
/// and the marshalling onto the UI thread both belong here.
/// </summary>
public sealed partial class VaultStatusViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly VaultGateService _gate;
    private readonly VaultHealthMonitor _healthMonitor;
    private readonly Dispatcher _dispatcher;

    private readonly CredentialsViewModel _credentials;
    private readonly RoutesViewModel _routes;
    private readonly McpFunnelViewModel _funnels;
    private readonly SettingsViewModel _settings;

    public VaultStatusViewModel(
        ConfigStoreCache configStoreCache,
        VaultGateService gate,
        VaultHealthMonitor healthMonitor,
        CredentialsViewModel credentials,
        RoutesViewModel routes,
        McpFunnelViewModel funnels,
        SettingsViewModel settings)
    {
        _configStoreCache = configStoreCache;
        _gate = gate;
        _healthMonitor = healthMonitor;
        _credentials = credentials;
        _routes = routes;
        _funnels = funnels;
        _settings = settings;

        _dispatcher = Dispatcher.CurrentDispatcher;

        // The monitor runs on a thread-pool thread, so every touch of a bound property below has
        // to be marshalled — WPF throws on a cross-thread property change, and it would do it from
        // inside a background service where nothing is watching.
        _configStoreCache.AccessChanged += access => _dispatcher.BeginInvoke(() => Apply(access));

        Apply(_configStoreCache.Access);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    private bool _isWritable = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    private bool _isOutOfSync;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";

    /// <summary>The per-manager advice, shown behind "Why can't I edit?".</summary>
    [ObservableProperty] private string _guidance = "";

    public bool IsDegraded => !IsWritable || IsOutOfSync;

    /// <summary>Re-probes now, for the "I've unlocked it" button.</summary>
    [RelayCommand]
    private async Task CheckAgainAsync()
    {
        await Task.Run(() => _healthMonitor.CheckAsync());
        Apply(_configStoreCache.Access);
    }

    /// <summary>
    /// Discards in-memory state and re-reads the vault. The only way out of a half-applied save,
    /// and of a secret edited in the password manager's own UI — neither of which the app can
    /// resolve on its own, since there is no change feed to watch.
    /// </summary>
    [RelayCommand]
    private async Task ReloadFromVaultAsync()
    {
        await _configStoreCache.ReloadAsync();

        _credentials.Reload();
        _routes.Reload();
        _funnels.Reload();
        _settings.Reload();

        Apply(_configStoreCache.Access);
    }

    private void Apply(VaultAccess access)
    {
        IsWritable = access == VaultAccess.Writable;
        IsOutOfSync = _configStoreCache.IsOutOfSync;

        var manager = VaultLockGuidance.DisplayName(_gate.Status.Selected);

        _credentials.CanEdit = IsWritable;
        _routes.CanEdit = IsWritable;
        _funnels.CanEdit = IsWritable;
        _settings.CanEdit = IsWritable;

        if (!IsWritable)
        {
            Headline = $"{manager} is locked — editing and token refresh are paused.";
            Detail = DescribeWhatBreaksNext();
            Guidance = VaultLockGuidance.StayingUnlockedSteps(_gate.Status.Selected);
            return;
        }

        if (IsOutOfSync)
        {
            Headline = "Some changes reached the vault and some did not.";
            Detail = "Retry the save, or reload from the vault to discard what did not.";
            Guidance = "";
            return;
        }

        Headline = "";
        Detail = "";
        Guidance = "";
    }

    /// <summary>
    /// How long there is before the lock actually costs something. Without this the banner is a
    /// warning with no stakes; with it the user can tell "fix this now" from "fix it later".
    /// </summary>
    private string DescribeWhatBreaksNext()
    {
        var expiring = _configStoreCache.Current.Credentials
            .Where(c => c.Token is not null)
            .Select(c => c.Token!.ExpiresAtUtc)
            .OrderBy(expiry => expiry)
            .ToList();

        if (expiring.Count == 0)
        {
            return "No OAuth credentials are affected. API-key routes keep working normally.";
        }

        var soonest = expiring[0];

        if (soonest <= DateTimeOffset.UtcNow)
        {
            return "At least one OAuth token has already expired, so routes using it are failing. "
                   + "API-key routes are unaffected.";
        }

        var remaining = soonest - DateTimeOffset.UtcNow;

        var span = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours} hour(s)"
            : $"{Math.Max(1, (int)remaining.TotalMinutes)} minute(s)";

        return $"OAuth routes keep working for about {span}, until the first token expires. "
               + "API-key routes are unaffected.";
    }
}
