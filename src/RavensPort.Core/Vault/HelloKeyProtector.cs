using System.Security.Cryptography;
using RavensPort.Core.Diagnostics;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace RavensPort.Core.Vault;

/// <summary>
/// Keeps the Proton Pass session key on disk in a form only a Windows Hello gesture can open.
///
/// **Why not the Credential Manager.** The obvious answer — <c>CredWrite</c>, or DPAPI — stores the
/// key encrypted at rest under the user's profile, and any process running as that user reads it
/// back silently. No prompt, no gesture, no trace. That is strictly worse than what it replaces
/// here, where the key is not on disk at all, and a UI that said "protected by Windows Hello" over
/// it would be untrue.
///
/// **What this does instead.** <see cref="KeyCredentialManager"/> creates an RSA-2048 key held by
/// the TPM where one exists. The private key cannot be exported; the only thing an app may do is
/// ask for a signature, and that request always shows the Hello prompt. So the session key is
/// encrypted with a key derived from a signature that cannot be obtained without the gesture:
/// skipping the prompt does not yield the wrong answer, it yields nothing.
///
/// That binding is the entire point. <c>UserConsentVerifier</c> would also show a Hello prompt, but
/// it returns a boolean and protects nothing — a patched binary, or a caller that simply does not
/// ask, reaches the data anyway. Verifying and decrypting have to be the same operation.
///
/// **Two limits worth stating plainly, because the UI must not overclaim.**
///
/// The credential is scoped to the Windows account, not to RavensPort. RavensPort is an unpackaged
/// Win32 app, so the credential service finds no AppContainer boundary and falls back to user-level
/// scoping; another program running as the same user that knows the credential name can ask to sign
/// with it. It cannot do so quietly — the user sees a Hello prompt they did not initiate — but the
/// boundary is "you would notice", not "it cannot happen".
///
/// And this relies on the signature over a fixed challenge being the same every time, which holds
/// because Hello signs with PKCS#1 v1.5. That is an implementation detail of Windows, not a
/// contract. If it ever became randomised, every key stored this way would stop opening — which is
/// why pasting the key by hand stays supported and is the documented way back.
/// </summary>
public sealed class HelloKeyProtector(ActivityLog activityLog)
{
    /// <summary>
    /// The floor for the credential APIs, checked rather than declared.
    ///
    /// The app as a whole supports Windows 10 1809, so this type cannot be marked as requiring
    /// 2004 — that would make every call site a warning and push the problem outwards. Guarding
    /// here instead keeps the version rule in the one place that has the version requirement, and
    /// gives the platform analyzer something it can actually verify.
    /// </summary>
    private static bool IsSupportedWindows => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>
    /// Names the credential in the user's Hello store. Also what another app would have to guess to
    /// reach it — which is no defence, and is not treated as one.
    /// </summary>
    private const string CredentialName = "RavensPort.ProtonPassSessionKey";

    /// <summary>
    /// Beside the session it protects, so signing out takes it too:
    /// <see cref="ProtonPassSession.Wipe"/> removes the directory whole.
    /// </summary>
    public static string BlobPath(string sessionDirectory) => Path.Combine(sessionDirectory, "hello.bin");

    /// <summary>Whether this machine can do it at all — Hello enrolled, with a PIN at minimum.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return IsSupportedWindows && await KeyCredentialManager.IsSupportedAsync();
        }
        catch
        {
            // Hello is absent, disabled by policy, or the projection is unavailable. All of them
            // mean the same thing to a caller: offer pasting instead.
            return false;
        }
    }

    public static bool HasProtectedKey(string sessionDirectory) => File.Exists(BlobPath(sessionDirectory));

    /// <summary>
    /// Stores <paramref name="sessionKey"/> so a Hello gesture can retrieve it. Prompts once, now,
    /// to create the credential and take the signature the encryption key comes from.
    /// </summary>
    public async Task ProtectAsync(string sessionDirectory, string sessionKey)
    {
        if (!IsSupportedWindows) throw new VaultCliException(NotSupported);

        // ReplaceExisting: the alternative is failing because a credential from a previous install
        // is still there, which the user can neither see nor clear.
        var creation = await KeyCredentialManager.RequestCreateAsync(
            CredentialName, KeyCredentialCreationOption.ReplaceExisting);

        if (creation.Status != KeyCredentialStatus.Success)
        {
            throw new VaultCliException(Explain(creation.Status, "set up"));
        }

        // Random per-install, and stored in the clear next to the ciphertext. It is not a secret:
        // its job is to be the fixed input whose signature only the TPM can produce.
        var challenge = RandomNumberGenerator.GetBytes(32);
        var derived = await SignForKeyAsync(creation.Credential, challenge, "set up");

        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext = System.Text.Encoding.UTF8.GetBytes(sessionKey);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using (var aes = new AesGcm(derived, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        CryptographicOperations.ZeroMemory(derived);
        CryptographicOperations.ZeroMemory(plaintext);

        Directory.CreateDirectory(sessionDirectory);
        File.WriteAllBytes(BlobPath(sessionDirectory), Pack(challenge, nonce, tag, ciphertext));

        activityLog.Log("VAULT stored the Proton Pass session key behind Windows Hello");
    }

    /// <summary>
    /// Retrieves the key, prompting for Hello. Returns null when there is nothing stored; throws
    /// when there is but it could not be opened, since those need different things said about them.
    /// </summary>
    public async Task<string?> UnprotectAsync(string sessionDirectory)
    {
        var path = BlobPath(sessionDirectory);
        if (!File.Exists(path)) return null;

        if (!IsSupportedWindows) throw new VaultCliException(NotSupported);

        var opened = await KeyCredentialManager.OpenAsync(CredentialName);

        if (opened.Status != KeyCredentialStatus.Success)
        {
            throw new VaultCliException(Explain(opened.Status, "unlock"));
        }

        var (challenge, nonce, tag, ciphertext) = Unpack(File.ReadAllBytes(path));
        var derived = await SignForKeyAsync(opened.Credential, challenge, "unlock");

        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(derived, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            // The gesture succeeded and the result still does not decrypt. The realistic cause is
            // the signature no longer being what it was — a reset Hello credential, or a Windows
            // change to how it signs. Either way this blob is scrap.
            throw new VaultCliException(
                "Windows Hello could not open the stored session key. It may have been reset since "
                + "the key was saved. Paste your session key instead, and save it again.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }

        activityLog.Log("VAULT unlocked the Proton Pass session key with Windows Hello");
        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Removes both halves — the stored blob and the Hello credential itself. Called on sign-out,
    /// so that "signed out" does not leave a credential in the user's Hello store forever.
    /// </summary>
    public async Task ForgetAsync(string sessionDirectory)
    {
        try
        {
            var path = BlobPath(sessionDirectory);
            if (File.Exists(path)) File.Delete(path);

            if (IsSupportedWindows) await KeyCredentialManager.DeleteAsync(CredentialName);
        }
        catch (Exception ex)
        {
            // Never allowed to fail a sign-out. Without the blob the credential opens nothing, and
            // without the credential the blob decrypts to nothing.
            activityLog.Log($"VAULT could not fully remove the Windows Hello key: {ex.Message}");
        }
    }

    /// <summary>
    /// The gesture, and the key that comes out of it. SHA-256 of the signature rather than the
    /// signature itself: 256 bytes of RSA output is not an AES key, and hashing is what turns one
    /// into the other without assuming anything about its structure.
    /// </summary>
    private static async Task<byte[]> SignForKeyAsync(KeyCredential credential, byte[] challenge, string verb)
    {
        var result = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));

        if (result.Status != KeyCredentialStatus.Success)
        {
            throw new VaultCliException(Explain(result.Status, verb));
        }

        CryptographicBuffer.CopyToByteArray(result.Result, out var signature);

        try
        {
            return SHA256.HashData(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private const string NotSupported =
        "Windows Hello needs Windows 10 version 2004 or newer. Paste your session key instead.";

    private static string Explain(KeyCredentialStatus status, string verb) => status switch
    {
        KeyCredentialStatus.UserCanceled =>
            $"Windows Hello was cancelled, so RavensPort did not {verb} the session key.",
        KeyCredentialStatus.NotFound =>
            "There is no Windows Hello key for RavensPort on this PC. Paste your session key instead.",
        KeyCredentialStatus.UserPrefersPassword =>
            "Windows Hello is not set up for this account. Paste your session key instead.",
        KeyCredentialStatus.SecurityDeviceLocked =>
            "Windows Hello is locked after too many failed attempts. Sign in to Windows again, or "
            + "paste your session key instead.",
        _ => $"Windows Hello could not {verb} the session key ({status}). Paste your session key instead.",
    };

    // ---- Blob layout: four length-prefixed byte arrays. Only the ciphertext is secret. ----------

    private static byte[] Pack(byte[] challenge, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write((byte)1);   // Format version, so a later change can be recognised rather than misread.

        foreach (var part in new[] { challenge, nonce, tag, ciphertext })
        {
            writer.Write(part.Length);
            writer.Write(part);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    private static (byte[] Challenge, byte[] Nonce, byte[] Tag, byte[] Ciphertext) Unpack(byte[] blob)
    {
        try
        {
            using var buffer = new MemoryStream(blob, writable: false);
            using var reader = new BinaryReader(buffer);

            if (reader.ReadByte() != 1) throw new InvalidDataException("Unknown format version.");

            byte[] Next()
            {
                var length = reader.ReadInt32();

                // Bounded before allocating: this file is attacker-writable in the sense that
                // anything running as the user can put bytes there, and a claimed length of
                // int.MaxValue should be a parse error rather than a 2 GB allocation.
                if (length is < 0 or > 4096) throw new InvalidDataException("Implausible field length.");

                return reader.ReadBytes(length);
            }

            return (Next(), Next(), Next(), Next());
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new VaultCliException(
                "The Windows Hello key file is damaged. Paste your session key instead, and save it again.");
        }
    }
}
