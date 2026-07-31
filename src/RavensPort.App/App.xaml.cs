using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using RavensPort.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RavensPort.App.Tray;
using RavensPort.App.ViewModels;
using RavensPort.Core.Mcp;
using RavensPort.Core.Platform;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.App;

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

    /// <summary>
    /// Kestrel can only be started once. The setup page can raise its ready event more than once —
    /// "Check again" after the gate has already opened, say — and a second Start() would throw.
    /// </summary>
    private bool _proxyStarted;

    /// <summary>The port Kestrel actually bound, so a reconnect can say when a vault disagrees.</summary>
    private int _boundPort;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one instance may run. A second one would fight the first over the fixed
        // ports — the proxy port and, more subtly, the fixed OAuth loopback ports, where
        // the loser fails with "conflicts with an existing registration on the machine".
        var mutex = new Mutex(initiallyOwned: true, "RavensPort_SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            // Not the owner, so it must never be released here — only disposed. The field is
            // left null so OnExit can tell "we own it" from "we're the duplicate".
            mutex.Dispose();
            MessageBox.Show(
                "RavensPort is already running — look for the padlock icon in the system tray.",
                "RavensPort", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var builder = WebApplication.CreateBuilder();

        // Deliberately no UseUrls here. The listen port lives in the vault along with everything
        // else, and the vault cannot be read until the password manager is unlocked — which may
        // involve a biometric prompt and cannot be made to happen before the host is built.
        // WebApplication.Urls stays writable right up until Start(), so the port is set in
        // StartProxyAsync once the store has actually been loaded.
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Long-lived MCP SSE/streamable-HTTP sessions shouldn't be dropped by Kestrel.
            options.Limits.KeepAliveTimeout = TimeSpan.FromHours(2);
        });

        builder.Services.AddRavensPort();

        builder.Services.AddSingleton<AutostartService>();

        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<VaultStatusViewModel>();
        builder.Services.AddSingleton<SetupViewModel>();
        builder.Services.AddSingleton<CredentialsViewModel>();
        builder.Services.AddSingleton<RoutesViewModel>();
        builder.Services.AddSingleton<McpFunnelViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIconManager>();

        // Build the host on a thread-pool thread (via Task.Run) rather than inline on the WPF
        // Dispatcher thread. Anything in this call graph that awaits async I/O would, with the
        // Dispatcher's SynchronizationContext still ambient, try to post its continuation back
        // onto this very thread — which is blocked waiting for it. Task.Run runs the delegate
        // with no SynchronizationContext, so nothing in it can capture the Dispatcher.
        _webApp = Task.Run(builder.Build).GetAwaiter().GetResult();

        var mainWindow = _webApp.Services.GetRequiredService<MainWindow>();
        var settingsViewModel = _webApp.Services.GetRequiredService<SettingsViewModel>();
        var mainWindowViewModel = _webApp.Services.GetRequiredService<MainWindowViewModel>();

        _trayIconManager = _webApp.Services.GetRequiredService<TrayIconManager>();
        _trayIconManager.Initialize(
            mainWindow,
            onAutostartChanged: () => Dispatcher.Invoke(settingsViewModel.Reload),
            confirmExit: ConfirmExitWithUnsavedChanges);
        _trayIconManager.SetState(TrayState.Starting);
        mainWindow.HiddenWhileGated += () => _trayIconManager.NotifyIdleWhileGated();

        // The setup page drives everything from here: it decides when there is a usable vault and
        // calls back to start the proxy. Wired before the first check so a gate that opens
        // immediately still gets a listener.
        var setupViewModel = _webApp.Services.GetRequiredService<SetupViewModel>();
        setupViewModel.ReadyToStart += StartProxyAsync;

        // "Sync now" with nothing pending checks the vault instead of doing nothing. The reload
        // itself lives on the vault-status view model, which depends on this one, so it arrives as
        // a hook rather than a reference.
        settingsViewModel.ReloadFromVaultRequested = () =>
            _webApp.Services.GetRequiredService<VaultStatusViewModel>().ReloadFromVaultAsync();

        // "Re-initialise from vault": empty everything held in memory and load it again, which is
        // the same work a reconnect does — the only difference is that the password manager was
        // never disconnected.
        settingsViewModel.ReinitialiseRequested = async () =>
        {
            var configStoreCache = _webApp.Services.GetRequiredService<ConfigStoreCache>();

            await configStoreCache.ResetAsync();
            await _webApp.Services.GetRequiredService<VaultStatusViewModel>().ReconnectAsync();

            _webApp.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();
        };

        // Dropping a record can take a route or funnel with it, so the tabs showing them have to
        // be rebuilt — their rows hold references to records that are no longer in the store.
        settingsViewModel.RecordsDropped += () =>
        {
            _webApp.Services.GetRequiredService<CredentialsViewModel>().Reload();
            _webApp.Services.GetRequiredService<RoutesViewModel>().Reload();
            _webApp.Services.GetRequiredService<McpFunnelViewModel>().Reload();
        };

        // Disconnecting from the Settings tab puts the whole window back to the setup page: with no
        // password manager there is no configuration, so the tabs would be four empty grids whose
        // every control fails — the same reason the app starts there.
        settingsViewModel.Disconnected += () =>
        {
            mainWindowViewModel.EnterSetupMode();
            _trayIconManager?.SetState(TrayState.SetupRequired);

            _ = setupViewModel.CheckAsync();
        };

        // Fire and forget on the Dispatcher rather than blocking it. The original deadlock hazard
        // was blocking this thread while a continuation tried to post back onto it; this never
        // blocks, and every piece of work below is still wrapped in Task.Run so nothing captures
        // the Dispatcher's SynchronizationContext.
        _ = setupViewModel.CheckAsync();
    }

    /// <summary>
    /// Loads the store, then binds and starts Kestrel. Separate from host <em>build</em> because
    /// the listen port lives in the vault, so it is not knowable until a password manager has been
    /// unlocked — which may involve a prompt the user takes a minute to notice.
    /// </summary>
    private async Task StartProxyAsync()
    {
        if (_proxyStarted)
        {
            await ReconnectAsync();
            return;
        }

        var configStoreCache = _webApp!.Services.GetRequiredService<ConfigStoreCache>();
        var mainWindowViewModel = _webApp.Services.GetRequiredService<MainWindowViewModel>();
        var setupViewModel = _webApp.Services.GetRequiredService<SetupViewModel>();

        var port = 0;

        try
        {
            // Task.Run for the same reason as the build above: this awaits vault I/O and then runs
            // hosted-service startup, and neither may capture the Dispatcher.
            await Task.Run(async () =>
            {
                await configStoreCache.InitializeAsync();

                port = configStoreCache.Current.Settings.ListenPort;

                _webApp.Urls.Clear();
                _webApp.Urls.Add($"http://127.0.0.1:{port}");

                // Must sit ahead of MapReverseProxy: it rejects callers that cannot present the
                // endpoint's proxy key, and blocks DNS-rebinding and browser-originated requests.
                // Without it, any process on this machine can spend the user's OAuth grant.
                _webApp.UseLocalAccessGuard();

                // After the guard, so funnel callers must present a proxy key like anyone else, and
                // before MapReverseProxy so /mcp is unambiguously the funnel's — routes are
                // forbidden from claiming that prefix.
                _webApp.UseMcpFunnelGate();
                _webApp.MapMcpFunnel();

                _webApp.MapReverseProxy();
                _webApp.Start();
            });
        }
        catch (Exception ex)
        {
            // A port clash used to be a dead end: the app shut down telling the user to edit a
            // file that no longer exists. The port lives in the vault now, so it can be changed
            // from the setup page while the proxy is down — which is the only moment it matters.
            _webApp.Services.GetService<ActivityLog>()?.LogError("Could not start the proxy", ex);
            setupViewModel.ReportPortConflict(port, ex.Message);
            _trayIconManager?.SetState(TrayState.SetupRequired);
            return;
        }

        _proxyStarted = true;
        _boundPort = port;

        // The tabs were built before this — they have to be, the window exists while the vault is
        // still locked — so every one of them was rendered from an empty store. Without this the
        // Credentials tab opens saying there are none, and stays that way until something else
        // reloads it. The other tabs hid the same bug: switching to them reloads them on the way in,
        // and Credentials is the tab already on screen.
        _webApp.Services.GetRequiredService<VaultStatusViewModel>().ReloadTabs();

        mainWindowViewModel.EnterNormalMode();
        _trayIconManager?.SetState(TrayState.Running);
    }

    /// <summary>
    /// Loads the store again after the user disconnected a password manager and connected one
    /// back. Kestrel is already bound and cannot be rebound in this process, so a listen port that
    /// differs in the newly connected vault takes effect at the next start — everything else, from
    /// routes to proxy keys, comes back immediately.
    /// </summary>
    private async Task ReconnectAsync()
    {
        var vaultStatusViewModel = _webApp!.Services.GetRequiredService<VaultStatusViewModel>();
        var mainWindowViewModel = _webApp.Services.GetRequiredService<MainWindowViewModel>();
        var setupViewModel = _webApp.Services.GetRequiredService<SetupViewModel>();
        var configStoreCache = _webApp.Services.GetRequiredService<ConfigStoreCache>();

        try
        {
            await vaultStatusViewModel.ReconnectAsync();
        }
        catch (Exception ex)
        {
            // Staying on the setup page is the right answer: the store did not load, so the tabs
            // would show a configuration that is not there.
            _webApp.Services.GetService<ActivityLog>()?.LogError("Could not reload the vault", ex);
            setupViewModel.ReportReconnectFailure(ex.Message);
            return;
        }

        _webApp.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

        if (configStoreCache.Current.Settings.ListenPort != _boundPort)
        {
            _webApp.Services.GetService<ActivityLog>()?.Log(
                $"STARTUP this vault asks for port {configStoreCache.Current.Settings.ListenPort}, but the proxy is "
                + $"already listening on {_boundPort} — restart RavensPort to move it");
        }

        mainWindowViewModel.EnterNormalMode();
        _trayIconManager?.SetState(TrayState.Running);
    }

    /// <summary>
    /// Asks before quitting with changes that are only in memory. Returns true when it is safe to
    /// proceed.
    ///
    /// This is the one place the deferred-sync design can actually cost the user something. Edits
    /// and token refreshes go ahead while the password manager is locked, and nothing is written
    /// to disk in the meantime, so exiting is the moment they stop existing — and a credential
    /// whose token rotated in that window needs reconnecting. Worth one dialog.
    ///
    /// Called from the tray's Exit rather than from OnExit, because OnExit runs after shutdown is
    /// already committed and there is no way back from it.
    /// </summary>
    private bool ConfirmExitWithUnsavedChanges()
    {
        var configStoreCache = _webApp?.Services.GetService<ConfigStoreCache>();
        if (configStoreCache is null || !configStoreCache.HasPendingChanges) return true;

        // One last attempt first. The manager is often unlocked by now — the user may have
        // unlocked it for something else entirely — and warning about losing changes that could
        // simply have been written would be a poor way to find that out.
        if (_webApp!.Services.GetService<VaultSyncQueue>() is { } syncQueue)
        {
            try
            {
                Task.Run(() => syncQueue.FlushAsync(TimeSpan.FromSeconds(15))).Wait(TimeSpan.FromSeconds(20));
            }
            catch
            {
                // The confirmation below is what actually protects the user.
            }

            if (!configStoreCache.HasPendingChanges) return true;
        }

        var manager = VaultLockGuidance.DisplayName(
            _webApp.Services.GetService<VaultGateService>()?.Status.Selected ?? VaultBackendKind.None);

        var answer = MessageBox.Show(
            $"Some changes have not been saved to {manager} yet."
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "They are only in memory, so exiting now discards them — and any credential whose "
            + "token was refreshed while it was locked will need to be reconnected."
            + $"{Environment.NewLine}{Environment.NewLine}"
            + $"Unlock {manager} and choose Cancel to save them first.",
            "RavensPort — unsaved changes",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return answer == MessageBoxResult.OK;
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
            $"RavensPort could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}"
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "If the listen port is already in use, close the other program using it — the port "
            + "is stored in your password manager and can be changed from the Settings tab.",
            "RavensPort", MessageBoxButton.OK, MessageBoxImage.Error);
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
            "RavensPort", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconManager?.Dispose();

        // Same reasoning as the Task.Run in OnStartup: StopAsync/DisposeAsync run on the
        // WPF Dispatcher thread here. If anything in Kestrel/hosted-service shutdown awaits
        // without ConfigureAwait(false) while the Dispatcher's SynchronizationContext is
        // still ambient, its continuation tries to post back onto this exact thread — which
        // is blocked waiting for it. That deadlock left RavensPort.exe running after "Exit"
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
                    // Belt and braces. The tray's Exit already flushed and asked, but this method
                    // also runs on paths that never went through it — a Windows shutdown, or the
                    // startup-failure path — and it ends in Environment.Exit, which would
                    // otherwise kill the process mid-write.
                    if (_webApp.Services.GetService<VaultSyncQueue>() is { } syncQueue)
                    {
                        await syncQueue.FlushAsync(TimeSpan.FromSeconds(20));
                    }

                    await _webApp.StopAsync(TimeSpan.FromSeconds(5));
                    await _webApp.DisposeAsync();
                }).Wait(TimeSpan.FromSeconds(35));
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
        // auth library, anything — would otherwise leave RavensPort.exe running invisibly
        // after "Exit", exactly what was reported. This makes shutdown unconditional.
        Environment.Exit(0);
    }
}
