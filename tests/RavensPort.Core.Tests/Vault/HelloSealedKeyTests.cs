using System.Security.Cryptography;
using System.Text;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The envelope the session key is stored in, and the derivation that turns a Hello signature into
/// the key that opens it.
///
/// None of this needs a TPM, a gesture, or a credential vault, so all of it runs on CI. That is the
/// point of the split: the properties worth regression-testing — no plaintext at rest, a wrong
/// signature failing closed, a tampered byte caught rather than decrypted around — are properties
/// of this file, not of the hardware.
/// </summary>
public class HelloSealedKeyTests
{
    private static byte[] Signature(string seed) => Encoding.UTF8.GetBytes($"pretend-rsa-signature-{seed}").ToArray();

    private static byte[] KeyFrom(string seed) => HelloSealedKey.DeriveKey(Signature(seed));

    // ---- Derivation --------------------------------------------------------------------------

    [Fact]
    public void DeriveKey_IsDeterministic()
    {
        // Load-bearing. The AES key is stored nowhere: every unlock re-derives it from a fresh
        // signature over the same challenge, so a derivation that varied would mean nothing ever
        // opened twice.
        Assert.Equal(KeyFrom("a"), KeyFrom("a"));
    }

    [Fact]
    public void DeriveKey_DiffersForDifferentSignatures()
    {
        Assert.NotEqual(KeyFrom("a"), KeyFrom("b"));
    }

    [Fact]
    public void DeriveKey_ProducesAnAesKeyLength()
    {
        Assert.Equal(32, KeyFrom("a").Length);
    }

    [Fact]
    public void DeriveKey_DoesNotReuseTheSignatureItself()
    {
        // The signature is the value an attacker who captured one gesture would hold. Hashing means
        // that value is not the thing sitting in memory as the key.
        var signature = Signature("a");

        Assert.NotEqual(signature, HelloSealedKey.DeriveKey(signature));
    }

    [Fact]
    public void DeriveKey_RejectsAnEmptySignature()
    {
        // A signer that reported success and returned nothing would otherwise yield a perfectly
        // usable key derived from no entropy — identical on every machine on earth.
        Assert.Throws<VaultCliException>(() => HelloSealedKey.DeriveKey([]));
    }

    // ---- The challenge -----------------------------------------------------------------------

    [Fact]
    public void NewChallenge_IsFullLengthAndFreshEveryTime()
    {
        var challenges = Enumerable.Range(0, 50)
            .Select(_ => Convert.ToHexString(HelloSealedKey.NewChallenge()))
            .ToList();

        Assert.Equal(50, challenges.Distinct().Count());
        Assert.All(challenges, c => Assert.Equal(HelloSealedKey.ChallengeBytes * 2, c.Length));
    }

    [Fact]
    public void ChallengeOf_ReturnsWhatWasSealed()
    {
        // Read before the gesture, because the signature is taken over it. If this drifted from
        // what Seal wrote, every unlock would sign the wrong message and nothing would open.
        var challenge = HelloSealedKey.NewChallenge();
        var blob = HelloSealedKey.Seal(KeyFrom("a"), challenge, "secret");

        Assert.Equal(challenge, HelloSealedKey.ChallengeOf(blob));
    }

    // ---- Round trip --------------------------------------------------------------------------

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("with spaces and / slashes + plus == padding")]
    [InlineData("ünïcödé — ключ — 鍵")]
    public void Seal_Open_RoundTripsExactly(string secret)
    {
        var key = KeyFrom("a");
        var blob = HelloSealedKey.Seal(key, HelloSealedKey.NewChallenge(), secret);

        Assert.Equal(secret, HelloSealedKey.Open(key, blob));
    }

    [Fact]
    public void Seal_Open_RoundTripsARealSessionKey()
    {
        // What is actually stored: 32 random bytes, base64. Byte-exact, because pass-cli SHA-256s
        // the string it is given and a single character of drift is an unopenable vault.
        var secret = ProtonPassSession.GenerateKey();
        var key = KeyFrom("a");

        Assert.Equal(secret, HelloSealedKey.Open(key, HelloSealedKey.Seal(key, HelloSealedKey.NewChallenge(), secret)));
    }

    // ---- What is at rest ---------------------------------------------------------------------

    [Fact]
    public void Seal_LeavesNoPlaintextInTheBlob()
    {
        // The blob is silently readable by anything running as this user. This is the assertion
        // that says what they get is worthless.
        const string secret = "SENTINEL-SESSION-KEY-VALUE";
        var blob = HelloSealedKey.Seal(KeyFrom("a"), HelloSealedKey.NewChallenge(), secret);

        BlobAssert.DoesNotContainSequence(Encoding.UTF8.GetBytes(secret), blob);

        // Also as text, in case a future format ever base64s or otherwise re-encodes the payload.
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(blob));
        Assert.DoesNotContain(secret, Convert.ToBase64String(blob));
    }

    [Fact]
    public void Seal_DoesNotStoreTheDerivedKey()
    {
        // The key is derived per unlock and belongs in no file. A blob that carried it would make
        // the gesture decorative.
        var key = KeyFrom("a");
        var blob = HelloSealedKey.Seal(key, HelloSealedKey.NewChallenge(), "secret");

        BlobAssert.DoesNotContainSequence(key, blob);
    }

    [Fact]
    public void Seal_ProducesADifferentBlobEveryTime()
    {
        // Same key, same challenge, same secret: a fresh nonce still has to make the ciphertext
        // differ. GCM is catastrophic under nonce reuse, so this is not cosmetic.
        var key = KeyFrom("a");
        var challenge = HelloSealedKey.NewChallenge();

        var blobs = Enumerable.Range(0, 25)
            .Select(_ => Convert.ToHexString(HelloSealedKey.Seal(key, challenge, "secret")))
            .ToList();

        Assert.Equal(25, blobs.Distinct().Count());
    }

    // ---- Failing closed ----------------------------------------------------------------------

    [Fact]
    public void Open_WithTheWrongKey_Throws_RatherThanReturningAnything()
    {
        // The whole security argument in one assertion: without the right signature there is no
        // partly-correct answer, only a failure.
        var blob = HelloSealedKey.Seal(KeyFrom("right"), HelloSealedKey.NewChallenge(), "secret");

        Assert.Throws<VaultCliException>(() => HelloSealedKey.Open(KeyFrom("wrong"), blob));
    }

    [Fact]
    public void Open_WithAKeyThatDiffersByOneBit_Throws()
    {
        var key = KeyFrom("a");
        var blob = HelloSealedKey.Seal(key, HelloSealedKey.NewChallenge(), "secret");

        var nearly = key.ToArray();
        nearly[0] ^= 0x01;

        Assert.Throws<VaultCliException>(() => HelloSealedKey.Open(nearly, blob));
    }

    [Fact]
    public void Open_RejectsATamperOfAnyByteOfTheBlob()
    {
        // Every byte, not a sample: the version, the length prefixes, the challenge, the nonce, the
        // tag and the ciphertext are all attacker-writable, and none of them may produce a value
        // that reaches pass-cli. The challenge is only covered because Seal passes it as associated
        // data — without that, editing it would be silently accepted here.
        var key = KeyFrom("a");
        var blob = HelloSealedKey.Seal(key, HelloSealedKey.NewChallenge(), "a-session-key");

        for (var i = 0; i < blob.Length; i++)
        {
            var tampered = blob.ToArray();
            tampered[i] ^= 0xFF;

            Assert.Throws<VaultCliException>(() => HelloSealedKey.Open(key, tampered));
        }
    }

    [Fact]
    public void Open_RejectsATruncatedBlob()
    {
        var key = KeyFrom("a");
        var blob = HelloSealedKey.Seal(key, HelloSealedKey.NewChallenge(), "a-session-key");

        for (var length = 0; length < blob.Length; length++)
        {
            Assert.Throws<VaultCliException>(() => HelloSealedKey.Open(key, blob[..length]));
        }
    }

    [Fact]
    public void Unpack_RejectsAnUnknownFormatVersion()
    {
        // So a future format change is recognised rather than misread as a corrupt blob of the
        // current one — or worse, parsed as one.
        var blob = HelloSealedKey.Seal(KeyFrom("a"), HelloSealedKey.NewChallenge(), "secret");
        blob[0] = HelloSealedKey.Version + 1;

        Assert.Throws<VaultCliException>(() => HelloSealedKey.Unpack(blob));
    }

    [Fact]
    public void Unpack_RefusesAnImplausibleFieldLength_RatherThanAllocatingIt()
    {
        // Anything running as this user can write here. A claimed length of int.MaxValue must be a
        // parse error, not a 2 GB allocation.
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(HelloSealedKey.Version);
        writer.Write(int.MaxValue);
        writer.Flush();

        Assert.Throws<VaultCliException>(() => HelloSealedKey.Unpack(buffer.ToArray()));
    }

    [Fact]
    public void Unpack_RefusesANegativeFieldLength()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(HelloSealedKey.Version);
        writer.Write(-1);
        writer.Flush();

        Assert.Throws<VaultCliException>(() => HelloSealedKey.Unpack(buffer.ToArray()));
    }

    [Fact]
    public void Unpack_RefusesAFieldLongerThanTheBound()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(HelloSealedKey.Version);
        writer.Write(HelloSealedKey.MaxFieldBytes + 1);
        writer.Flush();

        Assert.Throws<VaultCliException>(() => HelloSealedKey.Unpack(buffer.ToArray()));
    }

    [Fact]
    public void Unpack_RefusesAFieldShorterThanItsPrefixClaims()
    {
        // BinaryReader.ReadBytes returns short at end of stream instead of throwing, so without an
        // explicit check a truncated blob would reach AesGcm as a plausible-looking short nonce.
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write(HelloSealedKey.Version);
        writer.Write(32);
        writer.Write(new byte[8]);
        writer.Flush();

        Assert.Throws<VaultCliException>(() => HelloSealedKey.Unpack(buffer.ToArray()));
    }

    [Fact]
    public void Unpack_RefusesAnEmptyBlob()
    {
        Assert.Throws<VaultCliException>(() => HelloSealedKey.Unpack([]));
    }

    [Fact]
    public void Pack_Unpack_RoundTripsEveryField()
    {
        var challenge = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = RandomNumberGenerator.GetBytes(16);
        var ciphertext = RandomNumberGenerator.GetBytes(44);

        var (c, n, t, ct) = HelloSealedKey.Unpack(HelloSealedKey.Pack(challenge, nonce, tag, ciphertext));

        Assert.Equal(challenge, c);
        Assert.Equal(nonce, n);
        Assert.Equal(tag, t);
        Assert.Equal(ciphertext, ct);
    }
}
