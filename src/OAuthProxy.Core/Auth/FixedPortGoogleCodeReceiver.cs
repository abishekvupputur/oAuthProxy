using System.Diagnostics;
using System.Net;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using OAuthProxy.Core.Models;

namespace OAuthProxy.Core.Auth;

/// <summary>
/// Loopback code receiver on a fixed, always-the-same port — unlike Google.Apis.Auth's
/// built-in LocalServerCodeReceiver (random port per attempt), this gives us one stable
/// redirect URI the user can display and register in Google Cloud Console if they created
/// a "Web application" client type by mistake (exact match required there). "Desktop app"
/// clients don't need pre-registration at all — Google accepts any loopback port for those.
/// </summary>
internal sealed class FixedPortGoogleCodeReceiver(int port) : ICodeReceiver
{
    /// <summary>
    /// Fixed port means overlapping sign-ins collide (ERROR_ALREADY_EXISTS). Serialise them
    /// and give a readable error rather than a raw Win32 failure.
    /// </summary>
    private static readonly SemaphoreSlim FlowGate = new(1, 1);

    public string RedirectUri => $"http://127.0.0.1:{port}/authorize/";

    public async Task<AuthorizationCodeResponseUrl> ReceiveCodeAsync(AuthorizationCodeRequestUrl url, CancellationToken taskCancellationToken)
    {
        if (!await FlowGate.WaitAsync(TimeSpan.Zero, taskCancellationToken))
        {
            throw new InvalidOperationException(
                "Another sign-in is already in progress. Finish or cancel it in the browser, then try again.");
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);

        try
        {
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException(
                    $"Could not listen on {RedirectUri} ({ex.Message}). "
                    + "Another OAuthProxy instance or another program is using that port.", ex);
            }

            // Same shell-execute reasoning as LoopbackBrowser: verify it is really a web URL
            // before letting Windows decide what to launch.
            var authorizationUrl = url.Build().ToString();
            if (!UrlValidation.IsSafeToOpenInBrowser(authorizationUrl))
            {
                throw new InvalidOperationException(
                    "Refusing to open the authorization URL: it is not an http/https address.");
            }

            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(taskCancellationToken, timeoutCts.Token);

            var context = await listener.GetContextAsync().WaitAsync(linkedCts.Token);

            const string html = "<html><body>Authorization complete — you can close this window.</body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.OutputStream.Close();

            // Must use the already-decoded QueryString collection + dictionary ctor, exactly
            // as Google's own LocalServerCodeReceiver does. Passing the raw percent-encoded
            // query to the string ctor leaves the auth code encoded ("4%2F0A..." instead of
            // "4/0A..."), which then gets encoded a second time at the token endpoint and
            // Google rejects it with invalid_grant / "Malformed auth code".
            var queryString = context.Request.QueryString;
            var parameters = queryString.AllKeys
                .Where(k => k is not null)
                .ToDictionary(k => k!, k => queryString[k] ?? "");

            return new AuthorizationCodeResponseUrl(parameters);
        }
        finally
        {
            // Close() (not just Stop()) releases the HTTP.SYS registration, so an abandoned
            // or failed flow can't leave the fixed port bound for the next attempt.
            listener.Close();
            FlowGate.Release();
        }
    }
}
