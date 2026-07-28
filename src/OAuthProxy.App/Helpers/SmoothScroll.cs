using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OAuthProxy.App.Helpers;

/// <summary>
/// Makes the mouse wheel scroll the outer page rather than getting swallowed by a nested
/// scrollable (a DataGrid or log pane), which is what makes WPF pages feel "stuck".
/// The inner region still wins while it has somewhere left to scroll in that direction,
/// so log panes keep working and the page takes over once they bottom out.
///
/// Usage: &lt;ScrollViewer helpers:SmoothScroll.Enable="True"&gt;
/// </summary>
public static class SmoothScroll
{
    private const double WheelStep = 0.5;   // fraction of a wheel notch -> gentler than WPF's default jump

    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer) return;

        if ((bool)e.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer outer) return;

        // Let a nested scrollable keep the wheel while it can still move that way.
        if (e.OriginalSource is DependencyObject source && HasScrollableAncestor(source, outer, e.Delta))
        {
            return;
        }

        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta * WheelStep);
        e.Handled = true;
    }

    private static bool HasScrollableAncestor(DependencyObject source, ScrollViewer outer, int delta)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, outer))
        {
            if (current is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                var canScrollUp = delta > 0 && inner.VerticalOffset > 0;
                var canScrollDown = delta < 0 && inner.VerticalOffset < inner.ScrollableHeight;
                if (canScrollUp || canScrollDown) return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
