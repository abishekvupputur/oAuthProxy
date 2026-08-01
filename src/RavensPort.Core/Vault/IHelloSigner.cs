using System.Runtime.Versioning;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace RavensPort.Core.Vault;

/// <summary>Why a Hello operation did not produce a signature. Mapped from
/// <c>KeyCredentialStatus</c> so nothing above this layer has to name a WinRT type.</summary>
internal enum HelloFailure
{
    None,

    /// <summary>The user dismissed the prompt. Retryable, and nothing should be discarded over it.</summary>
    Cancelled,

    /// <summary>
    /// There is no such Hello credential. Distinct from every other failure because it is the one
    /// that makes a stored blob permanently unopenable — so it is the one that clears it.
    /// </summary>
    NotFound,

    /// <summary>Hello is not set up for this account, or policy forbids it.</summary>
    NotEnrolled,

    /// <summary>Too many failed attempts. Retryable after a Windows sign-in.</summary>
    DeviceLocked,

    Unknown,
}

/// <summary>
/// The outcome of asking Hello for a signature. A result rather than an exception because the
/// caller — <see cref="HelloKeyProtector"/> — owns every user-facing message, and a transport that
/// invented its own wording would put half the explanations somewhere nobody looks for them.
/// </summary>
internal readonly record struct HelloResult(HelloFailure Failure, byte[]? Signature)
{
    public bool Succeeded => Failure is HelloFailure.None;

    public static HelloResult Ok(byte[]? signature = null) => new(HelloFailure.None, signature);

    public static HelloResult Failed(HelloFailure failure) => new(failure, null);
}

/// <summary>
/// The Windows Hello gesture, as the one thing <see cref="HelloKeyProtector"/> cannot do without
/// hardware.
///
/// **Why this seam exists.** <see cref="KeyCredentialManager"/> needs an enrolled Hello credential,
/// a TPM, and a foreground window to parent its prompt to. A CI runner has none of the three, so
/// every test of the arrangement around it — that the stored blob is ciphertext, that losing either
/// half breaks the unlock, that a changed signature fails closed rather than returning rubbish —
/// would be untestable if the gesture were inlined. It is behind an interface so those tests can
/// substitute a deterministic signature and assert on everything else, which is where the bugs are.
///
/// The interface is deliberately narrow. It signs, and it says why it could not; it never sees the
/// session key, the blob, or the credential store, so a fake of it cannot accidentally weaken the
/// thing under test.
/// </summary>
internal interface IHelloSigner
{
    /// <summary>Whether this machine can hold a key behind a gesture at all.</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>Creates the credential, replacing any credential of the same name.</summary>
    Task<HelloResult> CreateAsync(string name);

    /// <summary>
    /// Opens the named credential and signs <paramref name="challenge"/> with it, prompting.
    /// Returns <see cref="HelloFailure.NotFound"/> when there is no such credential.
    /// </summary>
    Task<HelloResult> SignAsync(string name, byte[] challenge);

    /// <summary>Removes the credential. Never throws — sign-out is not allowed to fail.</summary>
    Task DeleteAsync(string name);
}

/// <summary>
/// The real thing: <see cref="KeyCredentialManager"/>, which creates an RSA-2048 key held by the
/// TPM where one exists. The private key cannot be exported; the only operation an app may ask for
/// is a signature, and that request always shows the Hello prompt.
///
/// This class is the only place in RavensPort that touches the WinRT credential API, and it holds
/// no logic beyond translating it. Everything worth asserting about the scheme lives on the other
/// side of <see cref="IHelloSigner"/>, where a test can reach it.
/// </summary>
internal sealed class KeyCredentialHelloSigner : IHelloSigner
{
    /// <summary>
    /// The floor for the credential APIs, checked rather than declared.
    ///
    /// The app as a whole supports Windows 10 1809, so this type cannot be marked as requiring
    /// 2004 — that would make every call site a warning and push the problem outwards. Guarding
    /// here instead keeps the version rule in the one place that has the version requirement.
    /// </summary>
    private static bool IsSupportedWindows => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            return IsSupportedWindows && await KeyCredentialManager.IsSupportedAsync();
        }
        catch
        {
            // Hello is absent, disabled by policy, or the projection is unavailable. All of them
            // mean the same thing to a caller: no key can be protected here.
            return false;
        }
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    public async Task<HelloResult> CreateAsync(string name)
    {
        if (!IsSupportedWindows) return HelloResult.Failed(HelloFailure.NotEnrolled);

        // ReplaceExisting: the alternative is failing because a credential from a previous install
        // is still there, which the user can neither see nor clear.
        var creation = await KeyCredentialManager.RequestCreateAsync(
            name, KeyCredentialCreationOption.ReplaceExisting);

        return creation.Status == KeyCredentialStatus.Success
            ? HelloResult.Ok()
            : HelloResult.Failed(Translate(creation.Status));
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    public async Task<HelloResult> SignAsync(string name, byte[] challenge)
    {
        if (!IsSupportedWindows) return HelloResult.Failed(HelloFailure.NotEnrolled);

        // Opening does not prompt; signing does. Both statuses matter, and NotFound from either
        // means the same thing to the caller: there is no key here that could ever open the blob.
        var opened = await KeyCredentialManager.OpenAsync(name);

        if (opened.Status != KeyCredentialStatus.Success)
        {
            return HelloResult.Failed(Translate(opened.Status));
        }

        var signed = await opened.Credential.RequestSignAsync(
            CryptographicBuffer.CreateFromByteArray(challenge));

        if (signed.Status != KeyCredentialStatus.Success)
        {
            return HelloResult.Failed(Translate(signed.Status));
        }

        CryptographicBuffer.CopyToByteArray(signed.Result, out var signature);
        return HelloResult.Ok(signature);
    }

    public async Task DeleteAsync(string name)
    {
        try
        {
            if (IsSupportedWindows) await KeyCredentialManager.DeleteAsync(name);
        }
        catch
        {
            // Never allowed to fail a sign-out. A credential left behind opens nothing once the
            // blob it keys is gone.
        }
    }

    private static HelloFailure Translate(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.UserCanceled => HelloFailure.Cancelled,
        KeyCredentialStatus.NotFound => HelloFailure.NotFound,
        KeyCredentialStatus.UserPrefersPassword => HelloFailure.NotEnrolled,
        KeyCredentialStatus.SecurityDeviceLocked => HelloFailure.DeviceLocked,
        _ => HelloFailure.Unknown,
    };
}
