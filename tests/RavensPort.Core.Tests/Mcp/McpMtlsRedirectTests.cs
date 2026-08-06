using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RavensPort.Core.Mcp;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// The hop into a proxy route is one HTTP request, and where it ends up is the upstream's choice.
/// Google Apps Script — the upstream that found this — answers every call with a 302 to
/// script.googleusercontent.com, and HttpClient follows it on the same handler that was built to
/// talk to this app's own mTLS listener.
///
/// So that handler is asked to validate a certificate belonging to someone else, and asked whether
/// to hand them the user's private client certificate. Getting the first wrong turned every Apps
/// Script source into "The SSL connection could not be established"; getting the second wrong
/// would have shipped the user's credential to Google on every call. Neither is visible from the
/// funnel's own tests, because nothing in a test redirects off the machine.
/// </summary>
public class McpMtlsRedirectTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("127.0.0.2", true)]     // the whole 127/8 block is this machine
    [InlineData("script.googleusercontent.com", false)]
    [InlineData("script.google.com", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LoopbackIsRecognisedByHost(string? host, bool expected)
    {
        Assert.Equal(expected, McpSourceConnectionPool.IsLoopback(host));
    }

    [Fact]
    public async Task TheClientCertificateIsOfferedToThisAppAndToNobodyElse()
    {
        await using var host = await FunnelTestHost.StartAsync(mtls: true);

        var select = host.Pool.CreateMtlsHandler().SslOptions.LocalCertificateSelectionCallback;
        Assert.NotNull(select);

        var mine = select(this, "127.0.0.1", [], null, []);
        var theirs = select(this, "script.googleusercontent.com", [], null, []);

        Assert.NotNull(mine);
        Assert.Null(theirs);
    }

    [Fact]
    public async Task AValidPublicCertificateIsAcceptedAfterARedirectOffLoopback()
    {
        // The regression itself: a certificate that is not the pinned one, presenting cleanly, on
        // the leg the redirect leads to.
        await using var host = await FunnelTestHost.StartAsync(mtls: true);

        var validate = host.Pool.CreateMtlsHandler().SslOptions.RemoteCertificateValidationCallback;
        Assert.NotNull(validate);

        using var somebodyElse = SelfSigned("CN=script.googleusercontent.com");

        Assert.True(validate(this, somebodyElse, null, SslPolicyErrors.None));
    }

    [Fact]
    public async Task ACertificateThatIsNeitherPinnedNorValidIsRefused()
    {
        // Which is what any certificate on loopback other than the stored one looks like: no
        // public CA issues for 127.0.0.1, so the relaxation above cannot reach this case.
        await using var host = await FunnelTestHost.StartAsync(mtls: true);

        var validate = host.Pool.CreateMtlsHandler().SslOptions.RemoteCertificateValidationCallback;
        Assert.NotNull(validate);

        using var impostor = SelfSigned("CN=RavensPort MCP Client");

        Assert.False(validate(this, impostor, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public async Task TheStoredCertificateIsAcceptedDespiteHavingNoChain()
    {
        // The pin, still doing its job: this is the one certificate that never validates the
        // ordinary way and must be trusted anyway.
        await using var host = await FunnelTestHost.StartAsync(mtls: true);

        var validate = host.Pool.CreateMtlsHandler().SslOptions.RemoteCertificateValidationCallback;
        Assert.NotNull(validate);

        var stored = host.Pool.CreateMtlsHandler().SslOptions.ClientCertificates!.OfType<X509Certificate2>().Single();

        Assert.True(validate(this, stored, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
