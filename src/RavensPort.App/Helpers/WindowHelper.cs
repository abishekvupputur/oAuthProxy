using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RavensPort.App.Helpers;

internal static class WindowHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// Forces a dark title bar on the given window via the DWM API.
    /// Call from <c>SourceInitialized</c> so the HWND is already available.
    /// </summary>
    internal static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
