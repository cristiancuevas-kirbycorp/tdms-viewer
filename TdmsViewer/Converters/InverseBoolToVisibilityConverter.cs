using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TdmsViewer;

/// <summary>Collapses an element when the bound bool is true (used to hide group-node checkboxes).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
