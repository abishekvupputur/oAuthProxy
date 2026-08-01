using System.Text;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The two-way link between Windows Hello and the Credential Manager, which is the entire security
/// argument for storing the Proton Pass session key at all.
///
/// **The arrangement being pinned.** Two stores, and the key is in neither:
///
/// <list type="bullet">
/// <item>The Credential Manager holds <c>version ‖ challenge ‖ nonce ‖ tag ‖ ciphertext</c>. Windows
/// encrypts it at rest, but <c>CredRead</c> hands it to any process running as this user, silently.</item>
/// <item>Hello holds a non-exportable TPM key. It will sign, and signing always prompts.</item>
/// <item>The AES key is <c>SHA-256(signature over the challenge)</c>, computed fresh on every unlock
/// and written down nowhere.</item>
/// </list>
///
/// So: blob without gesture is ciphertext, gesture without blob is nothing to open, and a gesture
/// that produces a different signature than it used to fails closed rather than returning rubbish.
/// Every test below is one of those sentences.
///
/// **Why these run on CI.** Only the TPM is substituted, via <see cref="FakeHelloSigner"/> — the
/// encryption, the blob layout, the store interaction and the protector's own control flow are all
/// the shipping code. A fake signature is a faithful stand-in because the real one is also a
/// deterministic function of (key, credential, challenge); that is what PKCS#1 v1.5 over a fixed
/// challenge means, and it is the assumption the whole scheme rests on.
/// </summary>
public class HelloCredentialBindingTests : IDisposable
{
    private const string SessionKey = "SENTINEL-SESSION-KEY-Ic4t9Qv2";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-binding-{Guid.NewGuid()}");

    private readonly FakeHelloSigner _hello = new();
    private readonly InMemorySecretStore _store = new();
    private readonly ActivityLog _log;

    private string SessionDir => Path.Combine(_root, "session");
    private string Name => HelloKeyProtector.NameFor(SessionDir);

    /// <summary>
    /// One instance for the whole test, because that is what the app has: the protector is
    /// registered <c>AddSingleton</c> and lives for the process. A fresh instance per call would
    /// hide any state it started keeping between them — a cached signature, most obviously, which
    /// is precisely the regression <see cref="Unprotect_IsRepeatable_AndPromptsEveryTime"/> exists
    /// to catch and could not while this was a computed property.
    /// </summary>
    private readonly HelloKeyProtector _protector;

    public HelloCredentialBindingTests()
    {
        _log = new ActivityLog(Path.Combine(_root, "logs"));
        _protector = new HelloKeyProtector(_log, _hello, _store);
    }

    private HelloKeyProtector Protector => _protector;

    private Task ProtectAsync(string key = SessionKey) => Protector.ProtectAsync(SessionDir, key);

    // ---- The link works ----------------------------------------------------------------------

    [Fact]
    public async Task ProtectThenUnprotect_ReturnsTheExactKey()
    {
        await ProtectAsync();

        Assert.Equal(SessionKey, await Protector.UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task Unprotect_IsRepeatable_AndPromptsEveryTime()
    {
        await ProtectAsync();

        var before = _hello.SignCalls;

        Assert.Equal(SessionKey, await Protector.UnprotectAsync(SessionDir));
        Assert.Equal(SessionKey, await Protector.UnprotectAsync(SessionDir));

        // Two unlocks, two gestures. A cached key that skipped the second prompt would be the
        // quiet regression this whole arrangement exists to prevent.
        Assert.Equal(before + 2, _hello.SignCalls);
    }

    [Fact]
    public async Task Unprotect_NeverSucceedsWithoutAskingTheSigner()
    {
        await ProtectAsync();

        var before = _hello.SignCalls;
        var key = await Protector.UnprotectAsync(SessionDir);

        Assert.Equal(SessionKey, key);
        Assert.True(_hello.SignCalls > before, "the key came back without a gesture being requested");
    }

    [Fact]
    public async Task BothHalvesAreFiledUnderTheSameName()
    {
        // They are two parts of one arrangement. If the naming of either drifted, the result would
        // be a prompt that opens nothing — the exact state that strands a user.
        await ProtectAsync();

        Assert.True(_hello.HasCredential(Name));
        Assert.True(_store.Exists(Name));
        Assert.All(_hello.NamesSeen, seen => Assert.Equal(Name, seen));
        Assert.Equal([Name], _store.Names);
    }

    // ---- Neither half is sufficient alone ----------------------------------------------------

    [Fact]
    public async Task TheStoredBlobAlone_OpensNothing()
    {
        // Someone copies the credential out of Credential Manager. Without the TPM that keys it,
        // it is bytes.
        await ProtectAsync();

        var stolen = _store.Peek(Name);
        Assert.NotNull(stolen);

        var theirStore = new InMemorySecretStore();
        theirStore.Seed(Name, stolen);

        // They even have a Hello credential of the same name — a different TPM is still a different
        // signature, so the name buys them nothing.
        var theirSigner = new FakeHelloSigner();
        await theirSigner.CreateAsync(Name);

        await Assert.ThrowsAsync<VaultCliException>(
            () => new HelloKeyProtector(_log, theirSigner, theirStore).UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task TheHelloCredentialAlone_HasNothingToOpen()
    {
        // The mirror case: the gesture still works, the blob is gone. Null, not an error — this is
        // what a first run looks like, and the page offers a sign-in rather than a discard.
        await ProtectAsync();

        _store.Delete(Name);

        Assert.Null(await Protector.UnprotectAsync(SessionDir));
        Assert.True(_hello.HasCredential(Name));
    }

    [Fact]
    public async Task TheStoredBlobCarriesNoPlaintextKey()
    {
        await ProtectAsync();

        var blob = _store.Peek(Name)!;

        BlobAssert.DoesNotContainSequence(Encoding.UTF8.GetBytes(SessionKey), blob);
        Assert.DoesNotContain(SessionKey, Encoding.UTF8.GetString(blob));
    }

    // ---- Losing the Hello half ---------------------------------------------------------------

    [Fact]
    public async Task Unprotect_WhenTheHelloCredentialIsGone_Throws_AndClearsTheOrphanedBlob()
    {
        await ProtectAsync();

        _hello.LoseCredential(Name);

        await Assert.ThrowsAsync<VaultCliException>(() => Protector.UnprotectAsync(SessionDir));

        // Cleared, because it is permanently unopenable. Leaving it would keep offering an Unlock
        // button that can only ever fail, instead of the sign-in that would actually work.
        Assert.False(_store.Exists(Name));
        Assert.False(Protector.HasProtectedKey(SessionDir));
    }

    [Fact]
    public async Task Unprotect_AfterHelloIsReset_Throws_RatherThanReturningAWrongKey()
    {
        // Same credential name, new enrolment, different signatures. The realistic version of this
        // is a user resetting their PIN. There is no partly-correct answer available here.
        await ProtectAsync();

        _hello.ResetEnrolment();

        await Assert.ThrowsAsync<VaultCliException>(() => Protector.UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task Unprotect_AfterHelloIsReset_KeepsTheBlob_BecauseTheCredentialStillExists()
    {
        // Distinct from the NotFound case above on purpose. Here the signer answers, so the
        // protector cannot tell a reset enrolment from a corrupted blob, and deleting on a guess
        // would throw away a session that a repaired enrolment might still open.
        await ProtectAsync();

        _hello.ResetEnrolment();

        await Assert.ThrowsAsync<VaultCliException>(() => Protector.UnprotectAsync(SessionDir));

        Assert.True(_store.Exists(Name));
    }

    // ---- Retryable failures leave everything alone -------------------------------------------

    // Written as four facts rather than a Theory because HelloFailure is internal, and a public
    // test method cannot take it as a parameter.

    [Fact]
    public Task Unprotect_WhenCancelled_KeepsTheBlob() => RetryableFailureKeepsTheBlob(HelloFailure.Cancelled);

    [Fact]
    public Task Unprotect_WhenTheDeviceIsLocked_KeepsTheBlob() => RetryableFailureKeepsTheBlob(HelloFailure.DeviceLocked);

    [Fact]
    public Task Unprotect_WhenHelloIsNotEnrolled_KeepsTheBlob() => RetryableFailureKeepsTheBlob(HelloFailure.NotEnrolled);

    [Fact]
    public Task Unprotect_WhenTheGestureFailsForAnUnknownReason_KeepsTheBlob() => RetryableFailureKeepsTheBlob(HelloFailure.Unknown);

    private async Task RetryableFailureKeepsTheBlob(HelloFailure failure)
    {
        // A user who dismissed the prompt has not asked to lose their session. Only NotFound —
        // which means the blob can never open again — is allowed to discard anything.
        await ProtectAsync();

        _hello.SignFailure = failure;

        await Assert.ThrowsAsync<VaultCliException>(() => Protector.UnprotectAsync(SessionDir));

        Assert.True(_store.Exists(Name));

        // And it still opens once the reason goes away.
        _hello.SignFailure = null;
        Assert.Equal(SessionKey, await Protector.UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task Unprotect_WhenCancelled_SaysSoWithoutBlamingTheStoredKey()
    {
        await ProtectAsync();
        _hello.SignFailure = HelloFailure.Cancelled;

        var ex = await Assert.ThrowsAsync<VaultCliException>(() => Protector.UnprotectAsync(SessionDir));

        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Protect fails without leaving wreckage ----------------------------------------------

    [Fact]
    public async Task Protect_WhenTheCredentialCannotBeCreated_StoresNothing()
    {
        _hello.NextCreateFailure = HelloFailure.Cancelled;

        await Assert.ThrowsAsync<VaultCliException>(() => ProtectAsync());

        Assert.False(_store.Exists(Name));
        Assert.Equal(0, _store.WriteCalls);
    }

    [Fact]
    public async Task Protect_WhenTheGestureIsCancelled_StoresNothing_AndLeavesNoCredential()
    {
        _hello.SignFailure = HelloFailure.Cancelled;

        await Assert.ThrowsAsync<VaultCliException>(() => ProtectAsync());

        Assert.False(_store.Exists(Name));

        // The credential was created a moment earlier and now keys nothing. Leaving it would be a
        // Hello prompt the user could accept to no effect.
        Assert.False(_hello.HasCredential(Name));
    }

    [Fact]
    public async Task Protect_WhenTheStoreRefusesTheWrite_RemovesTheHelloCredential()
    {
        // The failure that used to be reported as success. A key believed stored and in fact not
        // stored is how a user is stranded after a restart, so it has to throw and clean up.
        _store.FailWrites = true;

        await Assert.ThrowsAsync<VaultCliException>(() => ProtectAsync());

        Assert.False(_store.Exists(Name));
        Assert.False(_hello.HasCredential(Name));
    }

    [Fact]
    public async Task Protect_Twice_ReplacesTheKey_AndOnlyTheNewOneOpens()
    {
        await ProtectAsync("first-key");
        var first = _store.Peek(Name)!;

        await ProtectAsync("second-key");
        var second = _store.Peek(Name)!;

        Assert.NotEqual(first, second);
        Assert.Equal("second-key", await Protector.UnprotectAsync(SessionDir));
        Assert.Single(_store.Names);
    }

    // ---- Presence checks are silent ----------------------------------------------------------

    [Fact]
    public async Task HasProtectedKey_TracksTheStore_WithoutPrompting()
    {
        Assert.False(Protector.HasProtectedKey(SessionDir));

        await ProtectAsync();
        var afterProtect = _hello.SignCalls;

        Assert.True(Protector.HasProtectedKey(SessionDir));

        // Bound by the setup page. A prompt from a property getter would fire on every refresh.
        Assert.Equal(afterProtect, _hello.SignCalls);
    }

    [Fact]
    public async Task Unprotect_WithNothingStored_ReturnsNull_WithoutPrompting()
    {
        Assert.Null(await Protector.UnprotectAsync(SessionDir));
        Assert.Equal(0, _hello.SignCalls);
    }

    // ---- Sign-out ----------------------------------------------------------------------------

    [Fact]
    public async Task Forget_RemovesBothHalves()
    {
        await ProtectAsync();

        await Protector.ForgetAsync(SessionDir);

        Assert.False(_store.Exists(Name));
        Assert.False(_hello.HasCredential(Name));
        Assert.Null(await Protector.UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task Forget_DoesNotThrow_WhenThereIsNothingToForget()
    {
        // Sign-out calls it unconditionally, and a sign-out is never allowed to fail.
        await Protector.ForgetAsync(SessionDir);
        await Protector.ForgetAsync(SessionDir);
    }

    // ---- Migration off the old file layout ---------------------------------------------------

    [Fact]
    public async Task ALegacyBlob_IsMovedIntoTheStore_AndStillOpensWithTheSameGesture()
    {
        // An install from before the move to Credential Manager. The format is unchanged, so the
        // same Hello credential opens it — which is why no gesture is needed to relocate it.
        await ProtectAsync();

        var blob = _store.Peek(Name)!;
        _store.Delete(Name);

        Directory.CreateDirectory(SessionDir);
        File.WriteAllBytes(HelloKeyProtector.LegacyBlobPath(SessionDir), blob);

        Assert.Equal(SessionKey, await Protector.UnprotectAsync(SessionDir));

        Assert.True(_store.Exists(Name));
        Assert.False(File.Exists(HelloKeyProtector.LegacyBlobPath(SessionDir)));
    }

    [Fact]
    public async Task ALegacyBlob_DoesNotOverwriteAKeyAlreadyInTheStore()
    {
        // Both present means the migration already happened and the file is a stale copy. The
        // store is the newer of the two, so it wins.
        await ProtectAsync();

        Directory.CreateDirectory(SessionDir);
        File.WriteAllBytes(HelloKeyProtector.LegacyBlobPath(SessionDir), [1, 2, 3]);

        Assert.Equal(SessionKey, await Protector.UnprotectAsync(SessionDir));
        Assert.False(File.Exists(HelloKeyProtector.LegacyBlobPath(SessionDir)));
    }

    [Fact]
    public async Task Protect_RemovesAnyLegacyBlobItSupersedes()
    {
        Directory.CreateDirectory(SessionDir);
        File.WriteAllBytes(HelloKeyProtector.LegacyBlobPath(SessionDir), [9, 9, 9]);

        await ProtectAsync();

        // One secret, one place. Two would mean sign-out only knew about one of them.
        Assert.False(File.Exists(HelloKeyProtector.LegacyBlobPath(SessionDir)));
    }

    // ---- Nothing leaks ------------------------------------------------------------------------

    [Fact]
    public async Task NeitherProtectNorUnprotect_WritesTheKeyToTheActivityLog()
    {
        await ProtectAsync();
        await Protector.UnprotectAsync(SessionDir);

        var lines = string.Join("\n", _log.GetRecent(100));

        Assert.DoesNotContain(SessionKey, lines);

        // The log should still say something happened — silence would be its own problem.
        Assert.Contains("Windows Hello", lines);
    }

    [Fact]
    public async Task Protect_WritesNothingIntoTheSessionDirectory()
    {
        // The session directory holds pass-cli's encrypted session and nothing else. A key stored
        // beside the data it encrypts is filing, not encryption.
        Directory.CreateDirectory(SessionDir);

        await ProtectAsync();

        Assert.Empty(Directory.GetFileSystemEntries(SessionDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
