using System.Security.Cryptography;
using System.Text;

namespace RavensPort.Core.Vault;

/// <summary>
/// The envelope the session key travels in: what gets written to the Credential Manager, and the
/// arithmetic that turns a Hello signature into the key that opens it.
///
/// Split out from <see cref="HelloKeyProtector"/> because it is the half that can be tested. The
/// gesture needs a TPM and a human; this needs neither, and it is where the properties that matter
/// actually live — that the blob carries no plaintext, that a wrong signature fails closed instead
/// of returning rubbish, and that a tampered byte is caught rather than decrypted around.
///
/// **AES-GCM, not AES-CBC or a bare stream cipher.** The blob sits somewhere any process running as
/// this user can overwrite. Without authentication, a caller who flipped bytes in the ciphertext
/// would get a corrupted session key back and hand it to pass-cli, which fails in a way that looks
/// like a Proton problem. The tag makes that a clean error instead.
/// </summary>
internal static class HelloSealedKey
{
    /// <summary>Format version, so a later change can be recognised rather than misread.</summary>
    internal const byte Version = 1;

    /// <summary>
    /// The challenge is per-seal and stored in the clear. It is not a secret: its only job is to be
    /// a fixed input whose signature only the TPM can produce. Fresh per seal anyway, so that two
    /// installs never derive the same key from the same Hello credential.
    /// </summary>
    internal const int ChallengeBytes = 32;

    /// <summary>
    /// No field in a well-formed blob comes near this. It is a bound on what a hostile writer can
    /// make this code allocate before it has authenticated anything.
    /// </summary>
    internal const int MaxFieldBytes = 4096;

    /// <summary>A fresh challenge, for a seal that is about to happen.</summary>
    internal static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ChallengeBytes);

    /// <summary>
    /// Turns a Hello signature into an AES key.
    ///
    /// SHA-256 of the signature rather than the signature itself: 256 bytes of RSA output is not an
    /// AES key, and hashing is what turns one into the other without assuming anything about its
    /// structure. It also means the signature — which is the one value an attacker who captured a
    /// single gesture would hold — is not itself the key sitting in memory.
    /// </summary>
    internal static byte[] DeriveKey(byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (signature.Length == 0)
        {
            // A signer that returned success and no bytes would otherwise produce a perfectly
            // usable key derived from nothing, identical on every machine.
            throw new VaultCliException("Windows Hello returned an empty signature.");
        }

        return SHA256.HashData(signature);
    }

    /// <summary>
    /// Encrypts <paramref name="secret"/> under <paramref name="derivedKey"/> and packs it with
    /// everything needed to reverse the operation except the key itself.
    /// </summary>
    internal static byte[] Seal(byte[] derivedKey, byte[] challenge, string secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        try
        {
            using var aes = new AesGcm(derivedKey, tag.Length);

            // The challenge goes in as associated data, not just alongside. It is stored in the
            // clear and is therefore the one field an attacker can edit freely — and editing it
            // changes which signature the next unlock asks for. Authenticating it means a blob
            // carrying a challenge someone swapped in fails as tampering, rather than sending the
            // user's finger at a challenge of the attacker's choosing.
            aes.Encrypt(nonce, plaintext, ciphertext, tag, challenge);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return Pack(challenge, nonce, tag, ciphertext);
    }

    /// <summary>
    /// The challenge a blob was sealed with — needed before the gesture, because the signature is
    /// taken over it.
    /// </summary>
    internal static byte[] ChallengeOf(byte[] blob) => Unpack(blob).Challenge;

    /// <summary>
    /// Reverses <see cref="Seal"/>. Throws rather than returning null on a wrong key: there is no
    /// such thing as a partly-correct answer here, and a caller that got one would pass it to
    /// pass-cli as a session key.
    /// </summary>
    internal static string Open(byte[] derivedKey, byte[] blob)
    {
        var (challenge, nonce, tag, ciphertext) = Unpack(blob);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(derivedKey, tag.Length);

            // Same associated data as Seal, so a blob whose challenge was edited fails here even
            // though the ciphertext itself is untouched.
            aes.Decrypt(nonce, ciphertext, tag, plaintext, challenge);
        }
        catch (CryptographicException)
        {
            // The gesture succeeded and the result still does not decrypt. The realistic causes are
            // a reset Hello credential, a Windows change to how it signs, or a blob someone
            // overwrote. None of them is recoverable from here.
            throw new VaultCliException(
                "Windows Hello could not open the stored session key. It may have been reset since "
                + "the key was saved. Discard this session and sign in again.");
        }

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // ---- Layout: a version byte, then four length-prefixed arrays. Only the ciphertext is secret.

    internal static byte[] Pack(byte[] challenge, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(Version);

        foreach (var part in new[] { challenge, nonce, tag, ciphertext })
        {
            writer.Write(part.Length);
            writer.Write(part);
        }

        writer.Flush();
        return buffer.ToArray();
    }

    internal static (byte[] Challenge, byte[] Nonce, byte[] Tag, byte[] Ciphertext) Unpack(byte[] blob)
    {
        try
        {
            using var buffer = new MemoryStream(blob, writable: false);
            using var reader = new BinaryReader(buffer);

            if (reader.ReadByte() != Version) throw new InvalidDataException("Unknown format version.");

            byte[] Next()
            {
                var length = reader.ReadInt32();

                // Bounded before allocating: anything running as this user can write to the
                // Credential Manager under this name, and a claimed length of int.MaxValue should
                // be a parse error rather than a 2 GB allocation.
                if (length is < 0 or > MaxFieldBytes)
                {
                    throw new InvalidDataException("Implausible field length.");
                }

                var part = reader.ReadBytes(length);

                // ReadBytes returns short at end of stream rather than throwing, so a truncated
                // blob would otherwise reach AesGcm as a plausible-looking short nonce.
                if (part.Length != length) throw new EndOfStreamException();

                return part;
            }

            return (Next(), Next(), Next(), Next());
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new VaultCliException(
                "The stored Windows Hello key is damaged. Discard this session and sign in again.");
        }
    }
}
