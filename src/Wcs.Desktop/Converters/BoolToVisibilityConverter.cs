using System.Globalization;
using Avalonia.Data.Converters;

namespace Wcs.Desktop.Converters;

/// <summary>
/// bool → Visible/Collapsed
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? true : false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true;
}
