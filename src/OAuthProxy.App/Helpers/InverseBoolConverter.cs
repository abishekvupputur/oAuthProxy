using System.Globalization;
using System.Windows.Data;

namespace OAuthProxy.App.Helpers;

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
