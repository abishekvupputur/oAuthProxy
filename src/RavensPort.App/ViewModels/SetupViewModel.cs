using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.App.ViewModels;

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
        "RavensPort",
        "store.dat");

    public ObservableCollection<ManagerCardViewModel> Managers { get; } = [];

    [ObservableProperty] private string _statusMessage = "Checking for a password manager…";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Set when both managers qualify and neither can be shown to hold the configuration.</summary>
    [ObservableProperty] private bool _needsAChoice;

    /// <summary>Set while the user has deliberately disconnected, so the page says so rather than
    /// presenting itself as a first-run setup.</summary>
    [ObservableProperty] private bool _isDisconnected;

    /// <summary>Set when the port could not be bound, which is fixable without a working proxy.</summary>
    [ObservableProperty] private bool _hasPortConflict;
    [ObservableProperty] private string _listenPort = "5559";

    [ObservableProperty] private bool _hasLegacyStore;

    /// <summary>Raised when the gate opens, so the host can start the proxy.</summary>
    public event Func<Task>? ReadyToStart;

    /// <summary>Set by the host when a vault connected after a disconnect could not be read.</summary>
    public void ReportReconnectFailure(string message) =>
        StatusMessage = $"Connected, but the vault could not be read: {message}";

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

            if (status.IsReady) await StartAsync("Loading your configuration from the vault…");
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
        if (IsBusy) return;

        IsBusy = true;

        try
        {
            // Asked on every launch when both managers qualify, by design: the choice is the one
            // piece of state that cannot live in the vault, and this app deliberately stores
            // nothing locally.
            Apply(gate.SelectBackend(card.Kind));
            activityLog.Log($"STARTUP using {card.Name} for this session");

            await StartAsync($"Loading the vault from {card.Name}…");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Creates a vault with the name the user chose, and starts using it.</summary>
    [RelayCommand]
    private async Task CreateVaultAsync(ManagerCardViewModel card)
    {
        if (IsBusy) return;

        var name = card.NewVaultName;

        // Caught here as well as in the provider so the answer is instant and says what to do
        // instead: a second vault of the same name is the one thing this page must not produce —
        // two vaults called RavensPort are indistinguishable in the picker, and the app would
        // pick between them by list order.
        if (card.Vaults.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = card.Profile.Trim().Length == 0
                ? $"'{name}' already exists in {card.Name}. Choose it above, or name a profile to make a separate one."
                : $"'{name}' already exists in {card.Name}. Choose it above, or use a different profile name.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Creating the '{name}' vault in {card.Name}…";

        try
        {
            var status = await Task.Run(() => gate.CreateVaultAsync(card.Kind, name));
            Apply(status);

            if (status.IsReady) await StartAsync($"Loading the '{name}' vault…");
        }
        catch (VaultAdoptionException ex)
        {
            // A name that is already taken, or blank. The user's answer is wrong rather than
            // broken, so the name stays in the box to be corrected.
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not create the '{name}' vault", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Uses a vault the user already has instead of creating RavensPort. The gate refuses
    /// anything that is neither empty nor already RavensPort's, and says why — see
    /// <see cref="VaultAdoption"/>.
    /// </summary>
    [RelayCommand]
    private async Task UseExistingVaultAsync(ManagerCardViewModel card)
    {
        if (IsBusy) return;

        var name = card.SelectedVaultName?.Trim() ?? "";
        if (name.Length == 0)
        {
            StatusMessage = "Choose a vault from the list first.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Checking the '{name}' vault in {card.Name}…";

        try
        {
            var status = await Task.Run(() => gate.UseExistingVaultAsync(card.Kind, name));
            Apply(status);

            if (status.IsReady) await StartAsync($"Loading the '{name}' vault…");
        }
        catch (VaultAdoptionException ex)
        {
            // The user's answer is wrong rather than broken — a typo, or a vault with their own
            // things in it. Says which, and leaves the name in the box to be corrected.
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not use the '{name}' vault", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens one of the vaults that already holds a configuration. Offered when more than one
    /// does — separate profiles, where guessing would open one and overwrite the other.
    /// </summary>
    [RelayCommand]
    private async Task UseNamedVaultAsync(VaultChoiceViewModel choice)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = $"Opening the '{choice.Name}' vault…";

        try
        {
            var status = await Task.Run(() => gate.UseExistingVaultAsync(choice.Kind, choice.Name));
            Apply(status);

            if (status.IsReady) await StartAsync($"Loading the '{choice.Name}' vault…");
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not open the '{choice.Name}' vault", ex);
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

        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = $"Saving port {port} to the vault…";

        // Written straight to the vault: the proxy is not running, so there is no other way to
        // change it — which is precisely why the old "edit the file in %APPDATA%" advice had to go.
        try
        {
            var vault = gate.Selected;
            var store = await vault.LoadAsync();
            store.Settings.ListenPort = port;
            await vault.SaveAsync(store);

            HasPortConflict = false;
            await StartAsync($"Starting the proxy on port {port}…");
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not save the new listen port", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
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

    /// <summary>
    /// Hands off to the host, which reads the whole vault and starts the proxy — a CLI round trip
    /// per item, so seconds rather than an instant. The message says so: <see cref="Apply"/> has
    /// just written "Ready.", which would otherwise be the last thing on screen while the window
    /// sat there looking finished and doing nothing.
    /// </summary>
    private async Task StartAsync(string workingMessage)
    {
        if (ReadyToStart is not { } handler) return;

        StatusMessage = workingMessage;
        await handler();
    }

    private void Apply(VaultGateStatus status)
    {
        Managers.Clear();
        foreach (var manager in status.Statuses) Managers.Add(new ManagerCardViewModel(manager));

        NeedsAChoice = status.NeedsAChoice;
        IsDisconnected = gate.IsDisconnected;
        HasLegacyStore = File.Exists(LegacyStorePath);

        StatusMessage = status switch
        {
            { NeedsAChoice: true } when gate.IsDisconnected =>
                "Disconnected. Choose a password manager to connect to it again.",
            { NeedsAChoice: true } => "Both password managers are set up. Choose which one RavensPort should use.",
            { IsReady: true } => "Ready.",
            _ when status.Statuses.Any(s => s.Availability == VaultAvailability.VaultChoiceNeeded) =>
                "More than one vault holds a configuration. Choose which one to open.",
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
