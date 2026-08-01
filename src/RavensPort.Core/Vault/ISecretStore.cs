namespace RavensPort.Core.Vault;

/// <summary>
/// Where the sealed session key is kept. In production this is always
/// <see cref="WindowsCredentialStore"/> — there is no second implementation shipped, and the
/// interface exists so the tests can assert what <see cref="HelloKeyProtector"/> puts in a store
/// and what it does when either half of the arrangement goes missing.
///
/// Deliberately dumb. It stores bytes under a name; it does not know they are ciphertext, does not
/// encrypt or decrypt, and cannot prompt. Everything that makes the stored bytes worth nothing to
/// whoever reads them happens above this line, which is why a substituted store cannot weaken it.
/// </summary>
internal interface ISecretStore
{
    /// <summary>Whether something is stored under this name. Must never prompt: it is read from a
    /// property getter that the setup page binds.</summary>
    bool Exists(string target);

    /// <summary>The stored bytes, or null when there is nothing there. Null is an answer — a first
    /// run — and callers distinguish it from a failure to open something that is there.</summary>
    byte[]? Read(string target);

    /// <summary>Stores bytes, replacing whatever was there. Throws if the write did not take.</summary>
    void Write(string target, byte[] blob);

    /// <summary>Removes it, silently when there was nothing to remove.</summary>
    void Delete(string target);
}
