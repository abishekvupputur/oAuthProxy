namespace OAuthProxy.Core.Vault;

/// <summary>
/// Thrown when a write is attempted while the vault is locked or unreachable.
///
/// Deliberately not a "retry later" signal, and deliberately not queued: the vault is the only
/// copy of the config, so a write held in memory for an indefinite period is a change the user
/// believes is saved and that a restart would lose. Failing immediately keeps the app's claim
/// about what is stored honest, and the UI disables every write command on the same signal so
/// this is a backstop rather than something a user should normally be able to trigger.
/// </summary>
public sealed class VaultLockedException(VaultBackendKind kind, string? detail = null)
    : InvalidOperationException(BuildMessage(kind, detail))
{
    public VaultBackendKind Kind { get; } = kind;

    private static string BuildMessage(VaultBackendKind kind, string? detail)
    {
        var manager = kind switch
        {
            VaultBackendKind.OnePassword => "1Password",
            VaultBackendKind.ProtonPass => "Proton Pass",
            _ => "The password manager",
        };

        var message = $"{manager} is locked or unavailable, so nothing can be saved right now. "
                      + "Unlock it and try again.";

        return detail is null ? message : $"{message} ({detail})";
    }
}
