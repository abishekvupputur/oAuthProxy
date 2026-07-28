using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OAuthProxy.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OAuthProxy.App.Tray;
using OAuthProxy.App.ViewModels;
using OAuthProxy.Core.Platform;
using OAuthProxy.Core.Proxy;
using OAuthProxy.Core.Storage;

namespace OAuthProxy.App;

/// <summary>
/// WPF Application drives the process lifetime; it owns a Generic Host (Kestrel + YARP)
/// started here and stopped on exit. Both the web pipeline and the WPF UI share one DI
/// container. The app is always tray-resident — no window is shown on launch.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private WebApplication? _webApp;
    private TrayIconManager? _trayIconManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one instance may run. A second one would fight the first over the fixed
        // ports — the proxy port and, more subtly, the fixed OAuth loopback ports, where
        // the loser fails with "conflicts with an existing registration on the machine".
        var mutex = new Mutex(initiallyOwned: true, "OAuthProxy_SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            // Not the owner, so it must never be released here — only disposed. The field is
            // left null so OnExit can tell "we own it" from "we're the duplicate".
            mutex.Dispose();
            MessageBox.Show(
                "OAuthProxy is already running — look for the padlock icon in the system tray.",
                "OAuthProxy", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _singleInstanceMutex = mutex;

        // Safety net: this is an always-on tray app, so an unhandled exception anywhere must
        // not terminate the process (a Nextcloud login error used to kill it outright). Errors
        // are surfaced to the user and swallowed so the proxy and tray icon stay alive.
        DispatcherUnhandledException += (_, args) =>
        {
            ReportError("Unexpected error", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportError("Unexpected background error", args.Exception);
            args.SetObserved();
        };

        // Everything below can fail in ways that used to leave a live process with no tray
        // icon, no window, and no message: a listen port already in use throws out of
        // app.Start(), and DispatcherUnhandledException then marked it handled, so the app
        // "kept running" having never finished starting — while still holding the single-
        // instance mutex, so no later launch could get in either. Startup failure now means
        // an explanation and a real shutdown.
        try
        {
            StartHost();
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Shutdown();
        }
    }

    private void StartHost()
    {
        // Read the configured listen port before Kestrel binds — the port can't be changed
        // once bound, so this one synchronous read happens before host build. The "real"
        // config load into ConfigStoreCache still happens via ConfigStoreInitializerHostedService.
        var bootstrapStore = new SecureStore();
        var initialSettings = bootstrapStore.LoadAsync().GetAwaiter().GetResult().Settings;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{initialSettings.ListenPort}");
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Long-lived MCP SSE/streamable-HTTP sessions shouldn't be dropped by Kestrel.
            options.Limits.KeepAliveTimeout = TimeSpan.FromHours(2);
        });

        builder.Services.AddOAuthProxy();
        builder.Services.AddSingleton<AutostartService>();

        builder.Services.AddSingleton<CredentialsViewModel>();
        builder.Services.AddSingleton<RoutesViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIconManager>();

        // Build + start the host on a thread-pool thread (via Task.Run) rather than inline
        // on the WPF Dispatcher thread. Hosted-service startup (ConfigStoreInitializerHostedService)
        // awaits async I/O; if that ran with the Dispatcher's SynchronizationContext still
        // ambient, its continuation would try to post back onto this very thread, which is
        // blocked waiting for it — a deadlock. Task.Run runs the delegate with no
        // SynchronizationContext, so nothing in that call graph captures it.
        _webApp = Task.Run(() =>
        {
            var app = builder.Build();

            // Must sit ahead of MapReverseProxy: it rejects callers that cannot present the
            // local API key, and blocks DNS-rebinding and browser-originated requests. Without
            // it, any process on this machine can spend the user's OAuth grant.
            app.UseLocalAccessGuard();

            app.MapReverseProxy();
            app.Start();
            return app;
        }).GetAwaiter().GetResult();

        var mainWindow = _webApp.Services.GetRequiredService<MainWindow>();
        var settingsViewModel = _webApp.Services.GetRequiredService<SettingsViewModel>();

        _trayIconManager = _webApp.Services.GetRequiredService<TrayIconManager>();
        _trayIconManager.Initialize(
            mainWindow,
            onAutostartChanged: () => Dispatcher.Invoke(settingsViewModel.Reload));
    }

    private static void ReportStartupFailure(Exception ex)
    {
        // The host may be half-built, so don't count on resolving ActivityLog from it.
        try
        {
            new ActivityLog().LogError("Startup failed", ex);
        }
        catch
        {
            // ignored
        }

        MessageBox.Show(
            $"OAuthProxy could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}"
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "If the listen port is already in use, change it in %APPDATA%\\OAuthProxy or close "
            + "the other program using it.",
            "OAuthProxy", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ReportError(string title, Exception ex)
    {
        try
        {
            // Route through ActivityLog so it lands in the same folder the Settings tab
            // exposes, rather than a stray file in %TEMP%.
            _webApp?.Services.GetService<ActivityLog>()?.LogError(title, ex);
        }
        catch
        {
            // Logging must never itself take the app down.
        }

        MessageBox.Show($"{title}:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            "OAuthProxy", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconManager?.Dispose();

        // Same reasoning as the Task.Run in OnStartup: StopAsync/DisposeAsync run on the
        // WPF Dispatcher thread here. If anything in Kestrel/hosted-service shutdown awaits
        // without ConfigureAwait(false) while the Dispatcher's SynchronizationContext is
        // still ambient, its continuation tries to post back onto this exact thread — which
        // is blocked waiting for it. That deadlock left OAuthProxy.exe running after "Exit"
        // until force-killed. Task.Run drops the Dispatcher context for this whole shutdown
        // sequence, so nothing in it can capture it.
        if (_webApp is not null)
        {
            try
            {
                // Bounded overall wait too: StopAsync(5s) makes a best effort to respect that
                // timeout internally, but nothing here should be able to hang OnExit forever —
                // Wait() with its own timeout is the actual backstop.
                Task.Run(async () =>
                {
                    // Settings toggles kick off a save without awaiting it, and this method
                    // ends in Environment.Exit — so without draining first, flipping a
                    // checkbox and immediately choosing Exit lost the change.
                    var configStoreCache = _webApp.Services.GetService<ConfigStoreCache>();
                    if (configStoreCache is not null)
                    {
                        await configStoreCache.FlushAsync(TimeSpan.FromSeconds(3));
                    }

                    await _webApp.StopAsync(TimeSpan.FromSeconds(5));
                    await _webApp.DisposeAsync();
                }).Wait(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Whatever went wrong, exiting is still non-negotiable — fall through to
                // ReleaseMutex/Environment.Exit below rather than leaving the process stuck.
            }
        }

        // Non-null only in the instance that actually owns the mutex; the duplicate disposed
        // its handle at startup and left this null, so it never reaches ReleaseMutex.
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner (shouldn't happen given the guard above) — nothing to release.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);

        // Belt-and-suspenders: WPF exiting is supposed to let Main() return and the process
        // die naturally, but that only happens if every thread in the process is a background
        // thread. A stray foreground thread anywhere in the dependency graph — YARP, a Google
        // auth library, anything — would otherwise leave OAuthProxy.exe running invisibly
        // after "Exit", exactly what was reported. This makes shutdown unconditional.
        Environment.Exit(0);
    }
}
