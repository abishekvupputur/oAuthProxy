using CommunityToolkit.Mvvm.ComponentModel;
using OAuthProxy.Core.Vault;

namespace OAuthProxy.App.ViewModels;

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
        VaultAvailability.NotSignedIn => "Locked or signed out",
        VaultAvailability.VaultMissing => $"No '{VaultConstants.VaultName}' vault",
        VaultAvailability.Ready => "Ready",
        _ => "Not working",
    };

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
    /// The vault the user typed, for "use one I already have". Bound rather than passed as a
    /// command parameter so the text survives a failed attempt — the usual reason one fails is a
    /// misspelled name, and clearing it would make correcting it a retype.
    /// </summary>
    [ObservableProperty] private string _existingVaultName = "";

    // Exactly one section is shown per card, so the page never asks the user to read past advice
    // that does not apply to the state they are actually in.
    public bool ShowInstall => Availability == VaultAvailability.NotInstalled;
    public bool ShowSignIn => Availability is VaultAvailability.NotSignedIn or VaultAvailability.Faulted;
    public bool ShowCreateVault => Availability == VaultAvailability.VaultMissing;
}
