using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OAuthProxy.App.Helpers;

/// <summary>Shows an element when a bool is false. The negative twin of the built-in converter.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

/// <summary>
/// Negates a bool for binding. Used where the natural property is the positive one — IsBusy,
/// IsOutOfSync — and the control needs its opposite, so the view model does not have to carry a
/// second property that only exists to be the inverse of the first.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
