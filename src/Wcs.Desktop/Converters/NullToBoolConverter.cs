using System.Globalization;
using Avalonia.Data.Converters;

namespace Wcs.Desktop.Converters;

/// <summary>
/// 字符串非空 → true；null/空白 → false。用于"有内容才显示"的说明栏等场景。
/// </summary>
public class NullToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
