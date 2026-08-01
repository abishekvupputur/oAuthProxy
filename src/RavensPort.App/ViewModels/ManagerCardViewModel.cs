using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Vault;

namespace RavensPort.App.ViewModels;

/// <summary>
/// One password manager as the setup page shows it: what was found, what state it is in, and the
/// single next thing the user has to do about it.
/// </summary>
public sealed partial class ManagerCardViewModel(VaultStatus status) : ObservableObject
{
    public VaultBackendKind Kind { get; } = status.Kind;

    public string Name { get; } = VaultLockGuidance.DisplayName(status.Kind);

    public VaultAvailability Availability { get; } = status.Availability;

    /// <summary>Short state chip: the one-word answer to "where am I with this one".</summary>
    public string StateLabel { get; } = status.Availability switch
    {
        VaultAvailability.NotInstalled => "Not installed",

        // "Locked or signed out" hedges because for 1Password it genuinely could be either, and
        // only the CLI knows which. RavensPort owns its Proton Pass session, so there it does know:
        // not signed in, or signed in and waiting for the key — and the Detail line says which.
        VaultAvailability.NotSignedIn when status.Kind == VaultBackendKind.ProtonPass =>
            "Not signed in",
        VaultAvailability.NotSignedIn => "Locked or signed out",
        VaultAvailability.VaultMissing => $"No '{VaultConstants.VaultName}' vault",
        VaultAvailability.VaultChoiceNeeded => "Choose a vault",
        VaultAvailability.Ready => $"Ready — vault '{status.VaultName ?? VaultConstants.VaultName}'",
        _ => "Not working",
    };

    /// <summary>
    /// The vaults this card offers: named after RavensPort, and either empty or already
    /// holding a RavensPort configuration — the same test that decides whether picking one is
    /// accepted, so the list never offers something it will then refuse.
    ///
    /// Not every vault in the account. Listing all of them invites pointing a credential store at
    /// a personal vault, and an app that recites the contents of someone's password manager back
    /// at them is not one to trust with tokens. A vault adopted under some other name is not
    /// stranded by this: it is found by the configuration in it, which is what
    /// <see cref="VaultChoices"/> below offers.
    /// </summary>
    public IReadOnlyList<string> Vaults { get; } = status.AdoptableVaults ?? [];

    /// <summary>Vaults that already hold a RavensPort configuration — one per profile.</summary>
    public IReadOnlyList<VaultChoiceViewModel> VaultChoices { get; } =
        [.. (status.ConfiguredVaults ?? []).Select(name => new VaultChoiceViewModel(status.Kind, name))];

    /// <summary>
    /// The same vaults as buttons, for the "which one should RavensPort use" card. Offering the
    /// vaults rather than the manager answers the question the user actually has — which set of
    /// credentials am I opening — in one click instead of two.
    /// </summary>
    public IReadOnlyList<VaultChoiceViewModel> VaultButtons =>
        [.. Vaults.Select(name => new VaultChoiceViewModel(Kind, name))];

    public bool IsReady { get; } = status.IsReady;

    public string DetectedAt { get; } = status.ExePath is { Length: > 0 } path
        ? status.Version is { Length: > 0 } version ? $"{path}  (v{version})" : path
        : "Not found on this machine.";

    /// <summary>
    /// Whatever the CLI itself said. Preferred over anything this app could infer: it
    /// distinguishes locked from signed out from integration-disabled, and it is the only text
    /// here that reflects the actual reason.
    /// </summary>
    public string? Detail { get; } = status.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string InstallCommand { get; } = VaultLockGuidance.InstallCommand(status.Kind);

    public string DownloadUrl { get; } = VaultLockGuidance.DownloadUrl(status.Kind);

    public string SignInSteps { get; } = VaultLockGuidance.SignInSteps(status.Kind);

    public string? TokenCaveat { get; } = VaultLockGuidance.TokenCaveat(status.Kind);

    public bool HasTokenCaveat => !string.IsNullOrWhiteSpace(TokenCaveat);

    /// <summary>
    /// The vault picked from the list, for "use one I already have".
    ///
    /// A plain selection rather than an editable combo: the dark theme's ComboBox template has no
    /// PART_EditableTextBox, so an editable one renders — and stays — blank no matter what is
    /// bound to Text. Picking from the list and typing a new name are different actions anyway.
    /// </summary>
    [ObservableProperty] private string? _selectedVaultName =
        status.VaultName is { Length: > 0 } current
        && (status.AdoptableVaults ?? []).Contains(current, StringComparer.OrdinalIgnoreCase)
            ? current
            : null;

    /// <summary>
    /// The optional profile for a vault to create: blank makes RavensPort, "Work" makes
    /// "RavensPort Work".
    ///
    /// A profile rather than a free-text vault name. The name carries meaning — it is what marks
    /// the vault as this app's, and what the picker filters on — so letting someone type anything
    /// produced vaults that neither they nor RavensPort could recognise later.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewVaultName))]
    private string _profile = "";

    /// <summary>The vault the profile above would create. Shown, because a name assembled out of
    /// sight is one the user cannot check against what their password manager will show them.</summary>
    public string NewVaultName => VaultProfile.NameFor(Profile);

    // Exactly one section is shown per card, so the page never asks the user to read past advice
    // that does not apply to the state they are actually in.
    public bool ShowInstall => Availability == VaultAvailability.NotInstalled;
    public bool ShowSignIn => Availability is VaultAvailability.NotSignedIn or VaultAvailability.Faulted;
    public bool ShowVaultChoice => Availability == VaultAvailability.VaultChoiceNeeded;

    /// <summary>
    /// Whether RavensPort can install the CLI and drive the sign-in itself, rather than only
    /// telling the user how.
    ///
    /// True for Proton Pass alone. pass-cli signs in through a URL it prints, which the app can
    /// show; and it is open source, so the app may fetch it. 1Password's CLI has neither property —
    /// it authenticates with a Secret Key and account password at a terminal, and its licence does
    /// not permit redistribution — so its card keeps the written instructions.
    /// </summary>
    public bool SupportsInAppSignIn => Kind == VaultBackendKind.ProtonPass;

    public bool ShowInAppInstall => ShowInstall && SupportsInAppSignIn;
    public bool ShowInAppSignIn => ShowSignIn && SupportsInAppSignIn;
    
    public bool IsOnePassword => Kind == VaultBackendKind.OnePassword;
    public bool ShowOnePasswordSettings => IsOnePassword && (Availability == VaultAvailability.NotSignedIn || Availability == VaultAvailability.Faulted);

    public string OnePasswordAccountName
    {
        get => LocalSettings.Current.OnePasswordAccountName;
        set
        {
            if (LocalSettings.Current.OnePasswordAccountName != value)
            {
                LocalSettings.Current.OnePasswordAccountName = value;
                LocalSettings.Save();
                OnPropertyChanged(nameof(OnePasswordAccountName));
            }
        }
    }

    /// <summary>
    /// Picking a vault and creating one are offered together, in every state where either is
    /// possible — including on a card that is already Ready, which is the only way to move to a
    /// different vault without editing anything by hand. Each vault is its own set of credentials,
    /// routes and funnels.
    /// </summary>
    public bool ShowVaultActions =>
        Availability is VaultAvailability.VaultMissing or VaultAvailability.VaultChoiceNeeded
            or VaultAvailability.Ready;

    /// <summary>True when the account has no vaults to pick from, so the list is not shown empty.</summary>
    public bool HasVaults => Vaults.Count > 0;
}

/// <summary>One vault offered as a choice, carrying the manager it belongs to.</summary>
public sealed record VaultChoiceViewModel(VaultBackendKind Kind, string Name);
