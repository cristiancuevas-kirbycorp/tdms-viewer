using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TdmsViewer;

/// <summary>Converts a "#RRGGBB" string into a brush for color swatches.</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            // fall through to default
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
