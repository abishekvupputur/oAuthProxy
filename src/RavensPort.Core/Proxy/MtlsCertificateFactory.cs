using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RavensPort.Core.Proxy;

/// <summary>
/// The self-signed certificate that both ends of the mTLS listener present.
///
/// One certificate, used twice: Kestrel serves it and demands it back, and the only accepted
/// client is whoever holds the same private key. There is no CA and no chain to validate — both
/// sides pin the thumbprint — so a client certificate is neither more nor less than proof of
/// holding this file, which is what the user copies to the machine that may call the proxy.
/// </summary>
public static class MtlsCertificateFactory
{
    /// <summary>
    /// Password on the exported PFX. Not a secret and not pretending to be one: the file is the
    /// credential, and a constant password is what lets the app re-read its own copy unattended.
    /// Anyone holding the PFX holds the access it grants regardless of what is typed here.
    /// </summary>
    public const string PfxPassword = "ravensport";

    /// <summary>
    /// Deliberately <em>not</em> <see cref="X509KeyStorageFlags.EphemeralKeySet"/>, tempting as it
    /// is for a key that only has to outlive the process. Schannel cannot acquire server
    /// credentials from an in-memory key, so an ephemeral certificate binds and listens perfectly
    /// and then fails every single handshake with "the platform does not support ephemeral keys" —
    /// which reaches the client as a connection closed mid-handshake, with no status and nothing
    /// in the app's own log.
    ///
    /// Nor <see cref="X509KeyStorageFlags.Exportable"/>: nothing here re-exports (the PFX in
    /// settings is already the copy the user exports), and it only widens where the key can go.
    ///
    /// The default set imports the key into a container that CryptoAPI removes when the last
    /// handle closes, so <see cref="Mcp.KestrelMtlsState"/> disposing its certificate is what
    /// keeps this from leaving a key file behind on every start.
    /// </summary>
    private const X509KeyStorageFlags StorageFlags = X509KeyStorageFlags.DefaultKeySet;

    public static string GenerateClientCertificatePfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=RavensPort MCP Client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2"), new Oid("1.3.6.1.5.5.7.3.1")], false)); // Client Auth & Server Auth

        // Without a SAN the certificate is a server certificate in name only: every client that
        // does ordinary hostname validation — a browser, curl, an MCP host that is not this app —
        // rejects it before it ever gets to the pinning question, and CN has not been consulted
        // for that purpose in years. The proxy only ever binds loopback, so those are the names.
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var expire = DateTimeOffset.UtcNow.AddYears(10);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), expire);

        var pfxBytes = cert.Export(X509ContentType.Pfx, PfxPassword);
        return Convert.ToBase64String(pfxBytes);
    }

    /// <summary>
    /// Reads back a certificate stored by <see cref="GenerateClientCertificatePfx"/>. The single
    /// place the storage flags and the password are applied, so the copy Kestrel serves and the
    /// copy the funnel presents are loaded identically and their thumbprints match.
    /// </summary>
    public static X509Certificate2 Load(string base64Pfx)
    {
        try
        {
            return new X509Certificate2(Convert.FromBase64String(base64Pfx), PfxPassword, StorageFlags);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The stored mTLS certificate could not be read. Generate a new one on the Settings tab.", ex);
        }
    }
}
