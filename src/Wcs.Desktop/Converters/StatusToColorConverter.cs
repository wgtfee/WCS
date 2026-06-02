using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Wcs.Core.StateCenter.Models;

namespace Wcs.Desktop.Converters;

/// <summary>
/// 设备/任务/报警状态 → 颜色转换器
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            return status switch
            {
                "Online" or "Idle" or "Completed" or "Recovered" => Brushes.LimeGreen,
                "Running" or "Moving" or "Processing" => Brushes.DodgerBlue,
                "Offline" or "Error" or "Failed" => Brushes.Red,
                "Paused" or "Maintenance" => Brushes.Orange,
                "Queued" or "Created" => Brushes.Gray,
                "Active" => Brushes.OrangeRed,
                "Acknowledged" => Brushes.Gold,
                "Info" => Brushes.Silver,
                "Warning" => Brushes.Orange,
                "Critical" => Brushes.DarkRed,
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
