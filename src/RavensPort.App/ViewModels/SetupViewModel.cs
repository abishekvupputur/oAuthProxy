using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.App.Views;
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
    ProtonPassSession protonSession,
    ProtonPassAuthenticator protonAuthenticator,
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

    /// <summary>Whether a fresh manager check can run without interrupting another setup flow.</summary>
    public bool CanCheck => !IsBusy && !IsSigningIn;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

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
        if (IsBusy || IsSigningIn) return;

        NativeCliRunner.ResetInitialization();

        IsBusy = true;
        StatusMessage = "Checking…";

        try
        {
            // Asked once and cached: a WinRT capability check that cannot change while the app runs,
            // on the path that already blocks on two CLI probes.
            if (!_helloChecked)
            {
                _isHelloAvailable = await protonAuthenticator.IsHelloAvailableAsync();
                _helloChecked = true;
            }

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

    // ---- Proton Pass: install, unlock, sign in, sign out ------------------------------------
    //
    // All of this is Proton Pass only, and the asymmetry is not an oversight. 1Password's CLI has
    // no browser sign-in to drive — it wants a Secret Key and an account password typed at a
    // terminal — and its licence does not allow RavensPort to ship it. Offering a "Sign in" button
    // that could only ever open a text box asking for someone's 1Password master credentials would
    // be worse than the honest instructions the card already shows.

    /// <summary>The URL pass-cli printed. Shown, never launched — see <see cref="SignInProtonAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInUrl))]
    private string? _signInUrl;

    public bool HasSignInUrl => SignInUrl is { Length: > 0 };

    [ObservableProperty] private bool _isSigningIn;

    partial void OnIsSigningInChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    /// <summary>Cancels an in-flight sign-in, which kills the pass-cli process tree.</summary>
    private CancellationTokenSource? _signInCts;

    public bool HasSessionKey => protonSession.HasKey;

    /// <summary>
    /// Whether to show the Sign in button.
    ///
    /// Gated on Hello for a first sign-in, because signing in is what creates the session key and
    /// Hello is the only thing that can store it — the key is never shown, so there is no other way
    /// back into the session after a restart. A button that could only produce an unopenable
    /// session is worse than the explanation shown in its place.
    /// </summary>
    public bool CanShowSignInButton => HasSessionKey || (IsFirstSignIn && _isHelloAvailable);

    /// <summary>Whether to explain that Hello has to be set up before Proton Pass can be used here.</summary>
    public bool NeedsHelloSetup => IsFirstSignIn && !_isHelloAvailable;

    /// <summary>
    /// The one place that message is written — see <see cref="ProtonPassAuthenticator.HelloRequired"/>.
    /// An instance property despite being constant: WPF resolves binding paths through
    /// <c>TypeDescriptor</c>, which does not enumerate static members, so a static one would bind
    /// to nothing and show an empty block where the explanation should be.
    /// </summary>
    public string HelloRequiredMessage => ProtonPassAuthenticator.HelloRequired;

    /// <summary>
    /// Returning: a session is sitting on disk and only needs the key that opens it.
    ///
    /// Split from <see cref="IsFirstSignIn"/> because the two need opposite advice and opposite
    /// buttons. Showing Unlock and Generate side by side asked the user to know which of two
    /// situations they were in — and picking Generate in this one destroys the session they were
    /// trying to open.
    /// </summary>
    public bool NeedsSessionKey => !protonSession.HasKey && protonSession.HasSessionOnDisk;

    /// <summary>First time here: nothing to unlock, so a key has to be made before signing in.</summary>
    public bool IsFirstSignIn => !protonSession.HasKey && !protonSession.HasSessionOnDisk;

    /// <summary>
    /// Whether a Hello gesture can open this session — a key is stored and this PC can still do it.
    ///
    /// The availability half is cached rather than awaited per binding: it is an async WinRT call,
    /// and a property getter that blocks on one is a deadlock waiting for a slow TPM.
    /// </summary>
    public bool CanUnlockWithHello => _isHelloAvailable && protonAuthenticator.HasHelloKey;

    /// <summary>Whether this PC can do Hello at all, for the first-run explanation.</summary>
    public bool IsHelloAvailable => _isHelloAvailable;

    private bool _isHelloAvailable;
    private bool _helloChecked;

    /// <summary>The key-state flags move together and none of them is settable.</summary>
    private void NotifySessionStateChanged()
    {
        OnPropertyChanged(nameof(HasSessionKey));
        OnPropertyChanged(nameof(CanShowSignInButton));
        OnPropertyChanged(nameof(NeedsSessionKey));
        OnPropertyChanged(nameof(IsFirstSignIn));
        OnPropertyChanged(nameof(CanUnlockWithHello));
        OnPropertyChanged(nameof(NeedsHelloSetup));
        OnPropertyChanged(nameof(IsHelloAvailable));
    }

    /// <summary>Opens the session with a Hello gesture instead of a pasted key.</summary>
    [RelayCommand]
    private async Task UnlockWithHelloAsync()
    {
        if (IsBusy) return;

        IsBusy = true;

        try
        {
            // Through the consent window even though the button the user just pressed says
            // "Windows Hello" on it. The rule only protects anyone if it has no exceptions — see
            // HelloConsentWindow.
            if (!HelloConsentWindow.RequestUnlock(protonAuthenticator.UnlockWithHelloAsync))
            {
                StatusMessage = "Not unlocked. Discard this session and sign in again, or try Windows Hello again.";
                return;
            }

            NotifySessionStateChanged();
            Apply(gate.Status);

            if (gate.Status.IsReady) await StartAsync("Loading your configuration from the vault…");
        }
        catch (VaultCliException ex)
        {
            StatusMessage = ex.Message;
            NotifySessionStateChanged();
        }
        catch (Exception ex)
        {
            activityLog.LogError("Windows Hello unlock failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads pass-cli when the machine has none.</summary>
    [RelayCommand]
    private async Task InstallProtonCliAsync()
    {
        if (IsBusy) return;

        IsBusy = true;

        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);
            await protonAuthenticator.EnsureInstalledAsync(progress);

            await CheckAsync();
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not install the Proton Pass CLI", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Set once the user has asked to throw away a session they can no longer open.</summary>
    [ObservableProperty] private bool _isConfirmingDiscard;

    /// <summary>
    /// The way out for someone who has lost their session key.
    ///
    /// It has to live here, on the setup page. Sign out is on the Settings tab, which is only
    /// reachable once a vault is open — so pointing a locked-out user at it sent them to the far
    /// side of the door they could not open.
    /// </summary>
    [RelayCommand]
    private async Task DiscardSessionAsync()
    {
        if (!IsConfirmingDiscard)
        {
            IsConfirmingDiscard = true;
            StatusMessage = "Confirm to discard the locked session and start again.";
            return;
        }

        IsConfirmingDiscard = false;

        try
        {
            await protonAuthenticator.DiscardLocalSessionAsync();

            NotifySessionStateChanged();
            await CheckAsync();

            StatusMessage = "Session discarded. Choose Sign in to start again.";
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not discard the Proton Pass session", ex);
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void CancelDiscard()
    {
        IsConfirmingDiscard = false;
        StatusMessage = "Left as it is.";
    }

    /// <summary>
    /// Asks consent, then creates the session key and protects it — before any sign-in runs.
    ///
    /// Asked, not assumed: it is the moment RavensPort begins keeping something on this PC that was
    /// not there before. Cancelling leaves nothing behind, which is only true because this happens
    /// first. Offering it *after* a sign-in, as this used to, meant declining produced a live
    /// session whose key was in memory only and displayed nowhere — gone at the next restart, with
    /// nothing in the UI admitting it.
    ///
    /// Synchronous on the UI thread throughout: the consent window is modal, and the Hello prompt
    /// it raises needs a foreground window to attach to.
    /// </summary>
    private bool ProtectSessionKeyWithHello()
    {
        if (protonSession.HasKey && protonAuthenticator.HasHelloKey) return true;

        if (!_isHelloAvailable)
        {
            StatusMessage = HelloRequiredMessage;
            return false;
        }

        var consented = HelloConsentWindow.RequestSetup(protonAuthenticator.PrepareSessionKeyAsync);

        NotifySessionStateChanged();

        if (!consented)
        {
            StatusMessage =
                "Sign-in cancelled. Nothing was created — RavensPort needs Windows Hello to hold its "
                + "Proton Pass session key, because the key is never shown to you.";
        }

        return consented;
    }

    /// <summary>
    /// Runs the browser sign-in and shows the URL.
    ///
    /// The URL is deliberately not opened for the user. It carries a live single-use
    /// authentication handle, and launching it fires it at whichever browser happens to be default
    /// — quite possibly a profile signed in as someone else. Showing it lets them choose.
    /// </summary>
    [RelayCommand]
    private async Task SignInProtonAsync()
    {
        if (IsBusy || IsSigningIn) return;

        // Before IsSigningIn, so the consent window is not shown over a page already claiming a
        // sign-in is under way — cancelling here means none ever started.
        if (!ProtectSessionKeyWithHello()) return;

        IsSigningIn = true;
        SignInUrl = null;

        _signInCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);

            await protonAuthenticator.SignInAsync(
                url => SignInUrl = url,
                progress,
                _signInCts.Token);

            SignInUrl = null;

            var status = gate.Status;
            Apply(status);

            if (status.IsReady) await StartAsync("Loading your configuration from the vault…");
        }
        catch (OperationCanceledException)
        {
            SignInUrl = null;
            StatusMessage = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            SignInUrl = null;
            activityLog.LogError("Proton Pass sign-in failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            _signInCts?.Dispose();
            _signInCts = null;
            IsSigningIn = false;

            // A failed sign-in takes the key and the protected copy with it — see
            // ProtonPassAuthenticator.AbandonAsync — so the buttons this page shows have changed.
            NotifySessionStateChanged();
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    /// <summary>Copies a shown value — the sign-in URL, or a freshly generated key.</summary>
    [RelayCommand]
    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "Copied.";
        }
        catch (Exception ex)
        {
            // The clipboard is genuinely flaky — another process can hold it open — and this is
            // never worth failing anything over. The text is on screen to select by hand.
            StatusMessage = $"Could not copy: {ex.Message}";
        }
    }

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

        // Re-read on every evaluation: signing out happens on the Settings tab, which deletes the
        // session and clears the key without this page hearing about it directly.
        NotifySessionStateChanged();

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
