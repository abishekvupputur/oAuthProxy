using System.Security.Cryptography;
using System.Text;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Byte-sequence assertions. <c>Assert.DoesNotContain</c> on two arrays binds to the predicate
/// overload and quietly checks something else entirely, which would make every "no plaintext at
/// rest" assertion in this suite pass regardless.
/// </summary>
internal static class BlobAssert
{
    public static void DoesNotContainSequence(byte[] needle, byte[] haystack)
    {
        Assert.False(
            IndexOf(needle, haystack) >= 0,
            $"the blob contained the {needle.Length}-byte plaintext at offset {IndexOf(needle, haystack)}");
    }

    private static int IndexOf(byte[] needle, byte[] haystack)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return -1;

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matched = true;

            for (var i = 0; i < needle.Length && matched; i++)
            {
                matched = haystack[start + i] == needle[i];
            }

            if (matched) return start;
        }

        return -1;
    }
}

/// <summary>
/// A Windows Hello that runs on a build server.
///
/// It stands in for the TPM, and only for the TPM. The substitution is honest in the one property
/// the real scheme depends on: a signature is a deterministic function of (device key, credential
/// name, challenge), reproducible for as long as the credential exists and unrecoverable once it
/// does not. That is exactly what <c>KeyCredentialManager</c> guarantees with PKCS#1 v1.5 over a
/// non-exportable key, and it is what the protector is built on.
///
/// What it deliberately does not fake is the encryption, the blob layout, or the store — those are
/// the real implementations in every test that uses this, so a bug in them is still caught.
/// </summary>
internal sealed class FakeHelloSigner : IHelloSigner
{
    /// <summary>
    /// Stands in for the non-exportable private key. Rotating it is what a reset Hello enrolment
    /// looks like from outside: same credential name, different signatures, nothing decrypts.
    /// </summary>
    private byte[] _deviceKey = RandomNumberGenerator.GetBytes(32);

    private readonly HashSet<string> _credentials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the machine can do Hello at all.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Forced on the next <see cref="CreateAsync"/>, then cleared.</summary>
    public HelloFailure? NextCreateFailure { get; set; }

    /// <summary>Forced on every <see cref="SignAsync"/> until cleared. Not one-shot, because the
    /// cases it models — cancelled, locked out — persist until the user does something.</summary>
    public HelloFailure? SignFailure { get; set; }

    public int CreateCalls { get; private set; }
    public int SignCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    /// <summary>Every credential name this has been asked about, for the test that both halves of
    /// the arrangement are filed under one name.</summary>
    public List<string> NamesSeen { get; } = [];

    public bool HasCredential(string name) => _credentials.Contains(name);

    /// <summary>A reset Hello enrolment: the credential is still there, and it now signs differently.</summary>
    public void ResetEnrolment() => _deviceKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Someone removed the credential from outside RavensPort.</summary>
    public void LoseCredential(string name) => _credentials.Remove(name);

    public Task<bool> IsAvailableAsync() => Task.FromResult(Available);

    public Task<HelloResult> CreateAsync(string name)
    {
        CreateCalls++;
        NamesSeen.Add(name);

        if (NextCreateFailure is { } failure)
        {
            NextCreateFailure = null;
            return Task.FromResult(HelloResult.Failed(failure));
        }

        _credentials.Add(name);
        return Task.FromResult(HelloResult.Ok());
    }

    public Task<HelloResult> SignAsync(string name, byte[] challenge)
    {
        SignCalls++;
        NamesSeen.Add(name);

        if (SignFailure is { } failure) return Task.FromResult(HelloResult.Failed(failure));

        // Modelled on the real thing: opening a credential that is not there fails before any
        // prompt, and that failure is the one that makes a stored blob permanently unopenable.
        if (!_credentials.Contains(name)) return Task.FromResult(HelloResult.Failed(HelloFailure.NotFound));

        var message = Encoding.UTF8.GetBytes(name).Concat(challenge).ToArray();
        return Task.FromResult(HelloResult.Ok(HMACSHA256.HashData(_deviceKey, message)));
    }

    public Task DeleteAsync(string name)
    {
        DeleteCalls++;
        _credentials.Remove(name);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A Credential Manager that lives in a dictionary.
///
/// The real <see cref="WindowsCredentialStore"/> is exercised directly in
/// <c>HelloKeyStorageTests</c>, where a P/Invoke round trip is the thing under test. Here the store
/// is not what is being tested — what it holds, and what happens when it is emptied behind the
/// protector's back, is — so an in-memory one keeps those assertions independent of whether a CI
/// runner's logon session has a credential vault at all.
/// </summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simulates a store that will not take a write — a full or locked credential vault.</summary>
    public bool FailWrites { get; set; }

    public int WriteCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    public IReadOnlyCollection<string> Names => _entries.Keys;

    /// <summary>Reads without going through the interface, for assertions about what was stored.</summary>
    public byte[]? Peek(string target) => _entries.TryGetValue(target, out var blob) ? blob.ToArray() : null;

    /// <summary>Puts bytes there directly, to model a blob written by an earlier run.</summary>
    public void Seed(string target, byte[] blob) => _entries[target] = blob.ToArray();

    public bool Exists(string target) => _entries.ContainsKey(target);

    public byte[]? Read(string target) => _entries.TryGetValue(target, out var blob) ? blob.ToArray() : null;

    public void Write(string target, byte[] blob)
    {
        WriteCalls++;

        if (FailWrites) throw new InvalidOperationException("The credential store refused the write.");

        _entries[target] = blob.ToArray();
    }

    public void Delete(string target)
    {
        DeleteCalls++;
        _entries.Remove(target);
    }
}
