using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using OAuthProxy.Core.Platform;
using Application = System.Windows.Application;

namespace OAuthProxy.App.Tray;

/// <summary>
/// Uses plain WinForms NotifyIcon rather than a third-party WPF tray-icon library —
/// WPF-specific tray libraries (Hardcodet.NotifyIcon.Wpf, H.NotifyIcon.Wpf) have proven
/// flaky across .NET versions; System.Windows.Forms.NotifyIcon is the reliable baseline.
/// </summary>
public sealed class TrayIconManager(AutostartService autostartService) : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    public void Initialize(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;

        var contextMenu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Color.FromArgb(0x1A, 0x1A, 0x1A),
            ForeColor = Color.FromArgb(0xEB, 0xEB, 0xEB),
        };
        contextMenu.Items.Add("Open Settings", null, (_, _) => ShowMainWindow());

        var startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = autostartService.IsEnabled() };
        startupItem.Click += (_, _) =>
        {
            if (startupItem.Checked) autostartService.Enable();
            else autostartService.Disable();
        };
        contextMenu.Items.Add(startupItem);

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "OAuthProxy",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMainWindow();
        };
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
