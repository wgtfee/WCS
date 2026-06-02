using System.Globalization;
using Avalonia.Data.Converters;

namespace Wcs.Desktop.Converters;

/// <summary>
/// 布尔取反
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
