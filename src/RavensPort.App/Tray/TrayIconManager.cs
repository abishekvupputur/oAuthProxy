using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;

using Application = System.Windows.Application;

namespace RavensPort.App.Tray;

/// <summary>
/// What the app is currently doing, as far as the tray is concerned. The proxy no longer starts
/// unconditionally — it waits for a password manager — so "there is an icon" stopped meaning
/// "requests are being served", and the tooltip has to say which.
/// </summary>
public enum TrayState
{
    Starting,
    SetupRequired,
    Running,
    VaultLocked,
}

/// <summary>
/// Uses plain WinForms NotifyIcon rather than a third-party WPF tray-icon library —
/// WPF-specific tray libraries (Hardcodet.NotifyIcon.Wpf, H.NotifyIcon.Wpf) have proven
/// flaky across .NET versions; System.Windows.Forms.NotifyIcon is the reliable baseline.
/// </summary>
public sealed class TrayIconManager() : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private ToolStripItem? _openItem;
    private TrayState _state = TrayState.Starting;

    /// <param name="confirmExit">
    /// Asked before shutting down, and may refuse. Exit is the moment an unsaved change stops
    /// existing, so it is the one thing here that needs a way to say no — and it has to happen
    /// before Shutdown(), since OnExit runs when there is no longer any way back.
    /// </param>
    public void Initialize(
        MainWindow mainWindow,
        Func<bool>? confirmExit = null)
    {
        _mainWindow = mainWindow;

        var contextMenu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Color.FromArgb(0x1A, 0x1A, 0x1A),
            ForeColor = Color.FromArgb(0xEB, 0xEB, 0xEB),
        };
        _openItem = contextMenu.Items.Add("Open Settings", null, (_, _) => ShowMainWindow());

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) =>
        {
            if (confirmExit is null || confirmExit()) Application.Current.Shutdown();
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "RavensPort",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMainWindow();
        };

        SetState(_state);
    }

    /// <summary>
    /// Updates the tooltip and the first menu item. Tooltip-only rather than a second icon: a
    /// distinct overlay would be better, but the wrong-looking icon is a worse first impression
    /// than a clear tooltip, and this can be read without hovering over a 16px glyph.
    /// </summary>
    public void SetState(TrayState state)
    {
        _state = state;

        if (_notifyIcon is null) return;

        // NotifyIcon.Text throws above 63 characters, which is short enough that a well-meaning
        // longer message would crash the tray at runtime rather than at build time.
        _notifyIcon.Text = state switch
        {
            TrayState.Starting => "RavensPort — starting",
            TrayState.SetupRequired => "RavensPort — setup required",
            TrayState.VaultLocked => "RavensPort — vault locked",
            _ => "RavensPort",
        };

        if (_openItem is not null)
        {
            _openItem.Text = state is TrayState.SetupRequired or TrayState.Starting
                ? "Set up RavensPort…"
                : "Open Settings";
        }
    }

    /// <summary>
    /// A balloon for the one case the user cannot otherwise see: they closed the setup window
    /// without finishing, so the app is sitting in the tray serving nothing.
    /// </summary>
    public void NotifyIdleWhileGated()
    {
        _notifyIcon?.ShowBalloonTip(
            5000,
            "RavensPort is idle",
            "No proxy is running until a password manager is set up. Click the tray icon to finish.",
            ToolTipIcon.Info);
    }

    private static Icon LoadTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("tray.ico", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null) return new Icon(stream);
        }

        return SystemIcons.Application;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void Dispose() => _notifyIcon?.Dispose();
}
