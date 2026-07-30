using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OAuthProxy.Core.Diagnostics;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.App.ViewModels;

/// <summary>
/// The only page the app shows when it cannot reach a password manager.
///
/// It is a whole page rather than a dialog because there is genuinely nothing else to display:
/// every credential, route, key, and setting lives in the vault, so without one the tabs would be
/// four empty grids whose every button fails.
/// </summary>
public sealed partial class SetupViewModel(
    VaultGateService gate,
    ActivityLog activityLog) : ObservableObject
{
    /// <summary>The pre-vault store, kept only so the page can offer to delete it.</summary>
    private static readonly string LegacyStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OAuthProxy",
        "store.dat");

    public ObservableCollection<ManagerCardViewModel> Managers { get; } = [];

    [ObservableProperty] private string _statusMessage = "Checking for a password manager…";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set when both managers qualify and neither can be shown to hold the configuration.</summary>
    [ObservableProperty] private bool _needsAChoice;

    /// <summary>Set when the port could not be bound, which is fixable without a working proxy.</summary>
    [ObservableProperty] private bool _hasPortConflict;
    [ObservableProperty] private string _listenPort = "5559";

    [ObservableProperty] private bool _hasLegacyStore;

    /// <summary>Raised when the gate opens, so the host can start the proxy.</summary>
    public event Func<Task>? ReadyToStart;

    /// <summary>Set by the host when binding the listen port failed.</summary>
    public void ReportPortConflict(int port, string message)
    {
        ListenPort = port.ToString();
        HasPortConflict = true;
        StatusMessage = message;
    }

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Checking…";

        try
        {
            var status = await Task.Run(() => gate.EvaluateAsync());
            Apply(status);

            if (status.IsReady) await RaiseReadyAsync();
        }
        catch (Exception ex)
        {
            // The setup page is the last thing standing between the user and an app that does
            // nothing without explaining itself, so it absorbs everything.
            activityLog.LogError("Vault check failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChooseAsync(ManagerCardViewModel card)
    {
        // Asked on every launch when both managers qualify, by design: the choice is the one piece
        // of state that cannot live in the vault, and this app deliberately stores nothing locally.
        Apply(gate.SelectBackend(card.Kind));
        activityLog.Log($"STARTUP using {card.Name} for this session");

        await RaiseReadyAsync();
    }

    [RelayCommand]
    private async Task CreateVaultAsync(ManagerCardViewModel card)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = $"Creating the '{VaultConstants.VaultName}' vault in {card.Name}…";

        try
        {
            var status = await Task.Run(() => gate.CreateVaultAsync(card.Kind));
            Apply(status);

            if (status.IsReady) await RaiseReadyAsync();
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not create the {VaultConstants.VaultName} vault", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetryPortAsync()
    {
        if (!int.TryParse(ListenPort, out var port) || port is < 1 or > 65535)
        {
            StatusMessage = "Enter a port between 1 and 65535.";
            return;
        }

        // Written straight to the vault: the proxy is not running, so there is no other way to
        // change it — which is precisely why the old "edit the file in %APPDATA%" advice had to go.
        try
        {
            var vault = gate.Selected;
            var store = await vault.LoadAsync();
            store.Settings.ListenPort = port;
            await vault.SaveAsync(store);

            HasPortConflict = false;
            await RaiseReadyAsync();
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not save the new listen port", ex);
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenDownloadPage(ManagerCardViewModel card) => OpenUrl(card.DownloadUrl);

    /// <summary>
    /// Deletes the pre-vault store. Offered rather than done automatically: it is an encrypted
    /// file full of the user's secrets, and this version can no longer read it — silently
    /// destroying it on their behalf is not this app's call to make.
    /// </summary>
    [RelayCommand]
    private void DeleteLegacyStore()
    {
        try
        {
            if (File.Exists(LegacyStorePath)) File.Delete(LegacyStorePath);

            HasLegacyStore = false;
            StatusMessage = "Deleted the old configuration file.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete it: {ex.Message}";
        }
    }

    private async Task RaiseReadyAsync()
    {
        if (ReadyToStart is { } handler) await handler();
    }

    private void Apply(VaultGateStatus status)
    {
        Managers.Clear();
        foreach (var manager in status.Statuses) Managers.Add(new ManagerCardViewModel(manager));

        NeedsAChoice = status.NeedsAChoice;
        HasLegacyStore = File.Exists(LegacyStorePath);

        StatusMessage = status switch
        {
            { NeedsAChoice: true } => "Both password managers are set up. Choose which one OAuthProxy should use.",
            { IsReady: true } => "Ready.",
            _ when status.Statuses.Any(s => s.CanCreateVault) =>
                $"Almost there — create the '{VaultConstants.VaultName}' vault to finish.",
            _ when status.Statuses.All(s => s.Availability == VaultAvailability.NotInstalled) =>
                "No supported password manager found. Install 1Password or Proton Pass to continue.",
            _ => "Unlock or sign in to your password manager, then choose Check again.",
        };
    }

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the browser: {ex.Message}";
        }
    }
}
