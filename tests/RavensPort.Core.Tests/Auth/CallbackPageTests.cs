using RavensPort.Core.Auth;

namespace RavensPort.Core.Tests.Auth;

/// <summary>
/// The callback page is the only thing this app renders in a browser, and it renders it seconds
/// after the user typed a password somewhere. Its claims about itself have to stay true, the logo
/// has to survive being addressed by a string — the resource name embeds the assembly name, so a
/// rename that misses it would silently ship a page with a missing image and no build error — and
/// the failure page has to treat everything the provider sent as the remote input it is.
/// </summary>
public class CallbackPageTests
{
    [Fact]
    public void TheLogoIsEmbeddedAndInlinedAsAPng()
    {
        const string marker = "data:image/png;base64,";
        var start = CallbackPage.Html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the logo resource was not found, so the page rendered without it");

        var payload = CallbackPage.Html[(start + marker.Length)..];
        var bytes = Convert.FromBase64String(payload[..payload.IndexOf('"')]);

        // PNG signature: a data URI claiming image/png that isn't one would fail silently.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }

    [Fact]
    public void ThePageRunsNothingAndFetchesNothing()
    {
        // Both are promised in the page's own copy, and neither survives a careless edit on its
        // own: an added script tag or a remote font would make the text a lie.
        Assert.DoesNotContain("<script", CallbackPage.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", CallbackPage.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", CallbackPage.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheEncodingIsDeclaredSoTheCopyRendersAsWritten()
    {
        // Without this the browser guesses the system codepage and the em dashes arrive as
        // mojibake — which is exactly how this page used to look.
        Assert.Contains("<meta charset=\"utf-8\">", CallbackPage.Html, StringComparison.Ordinal);
        Assert.Contains("—", CallbackPage.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessAndFailureAreToldApartAtAGlance()
    {
        var failure = Failure("access_denied", null);

        // The class names appear in both pages' shared stylesheet — only the markup says which
        // badge was actually drawn.
        Assert.Contains("class=\"badge badge-ok\"", CallbackPage.Html, StringComparison.Ordinal);
        Assert.Contains("Authorization complete", CallbackPage.Html, StringComparison.Ordinal);

        Assert.Contains("class=\"badge badge-bad\"", failure, StringComparison.Ordinal);
        Assert.Contains("Authorization not completed", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"badge badge-ok\"", failure, StringComparison.Ordinal);

        // The one thing a user must not be told after a declined consent screen.
        Assert.DoesNotContain("Authorization complete", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProvidersWordsAreQuotedButNeverTrusted()
    {
        var failure = Failure("invalid_scope", "<img src=x onerror=\"alert(1)\">requested scope is invalid");

        Assert.Contains("requested scope is invalid", failure, StringComparison.Ordinal);
        Assert.Contains("&lt;img", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("onerror=\"", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ARamblingDescriptionCannotPushThePageOffScreen()
    {
        var failure = Failure("server_error", new string('x', 5_000));

        Assert.DoesNotContain(new string('x', 400), failure, StringComparison.Ordinal);
        Assert.Contains("…", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void NewlinesInADescriptionCannotBreakTheLayout()
    {
        var failure = Failure("server_error", "first line\r\n\r\nsecond line");

        Assert.Contains("first line second line", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderThatExplainsNothingStillGetsAPage()
    {
        var failure = Failure(null, null);

        Assert.Contains("Authorization not completed", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("The provider said", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders the failure page the way a redirect would, without an HttpListener: the response
    /// writer only adds status, content type and length, all of which are covered by the shape of
    /// the page itself being wrong if this markup is.
    /// </summary>
    private static string Failure(string? error, string? description) =>
        CallbackPage.FailureHtml(error, description);
}
