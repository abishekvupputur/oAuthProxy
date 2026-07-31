namespace RavensPort.Core.Vault;

/// <summary>
/// A save that reached the vault and failed there.
///
/// <see cref="PartiallyApplied"/> is the part that matters to callers. A whole-store save is
/// several CLI calls, so a failure part-way through leaves some items durable and some not.
/// ConfigStoreCache rolls its in-memory state back only when nothing was written — rolling back
/// after a partial write would make the next successful save delete records that are already
/// safely in the vault, which is worse than the drift it would be trying to prevent.
/// </summary>
public sealed class VaultSaveException(string message, bool partiallyApplied, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool PartiallyApplied { get; } = partiallyApplied;
}
