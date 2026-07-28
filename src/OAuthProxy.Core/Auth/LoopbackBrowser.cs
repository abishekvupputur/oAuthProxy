using System.Diagnostics;
using System.Net;
using System.Text;
using IdentityModel.OidcClient.Browser;

namespace OAuthProxy.Core.Auth;

/// <summary>
/// Provider-agnostic loopback redirect capture for the RFC 8252 installed-app OAuth2 flow.
/// Listens on a fixed localhost port so the redirect URI is stable and can be registered
/// up-front in a provider's console, opens the system browser to the consent screen, and
/// waits for the redirect to land on a local HttpListener.
/// </summary>
public sealed class LoopbackBrowser : IBrowser
{
    private const int RedirectPort = 51005;

    /// <summary>The single, stable redirect URI used for every non-Google provider.</summary>
    public static readonly string StaticRedirectUri = $"http://127.0.0.1:{RedirectPort}/callback/";

    /// <summary>
    /// The redirect port is fixed, so two overlapping sign-ins would collide on it
    /// (HttpListener fails with ERROR_ALREADY_EXISTS). Serialise flows in-process and fail
    /// the second one with an explanation instead of a raw Win32 error.
    /// </summary>
    private static readonly SemaphoreSlim FlowGate = new(1, 1);

    public string RedirectUri => StaticRedirectUri;

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        if (!await FlowGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new BrowserResult
            {
                ResultType = BrowserResultType.UnknownError,
                Error = "Another sign-in is already in progress. Finish or cancel it in the browser, then try again.",
            };
        }

        // Created here (not in the constructor) and always disposed below, so an abandoned
        // or failed flow can never leave the fixed port bound for the next attempt.
        var listener = new HttpListener();
        listener.Prefixes.Add(StaticRedirectUri);

        try
        {
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.UnknownError,
                    Error = $"Could not listen on {StaticRedirectUri} ({ex.Message}). "
                          + "Another OAuthProxy instance or another program is using that port.",
                };
            }

            Process.Start(new ProcessStartInfo(options.StartUrl) { UseShellExecute = true });

            var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromMinutes(5);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                return new BrowserResult
                {
                    ResultType = timeoutCts.IsCancellationRequested ? BrowserResultType.Timeout : BrowserResultType.UserCancel,
                };
            }

            const string html = "<html><body>Authorization complete — you can close this window.</body></html>";
            var responseBytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, cancellationToken);
            context.Response.OutputStream.Close();

            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                // AbsoluteUri is the documented fully-escaped form; ToString() is a display
                // form that unescapes some characters. OidcClient does its own decoding, so
                // it must receive the escaped URI exactly once.
                Response = context.Request.Url!.AbsoluteUri,
            };
        }
        catch (Exception ex)
        {
            return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = ex.Message };
        }
        finally
        {
            // Close() both stops the listener and releases the HTTP.SYS registration.
            listener.Close();
            FlowGate.Release();
        }
    }
}
