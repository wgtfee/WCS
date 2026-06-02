using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Models;

/// <summary>
/// 设备列表项 - 包装 DeviceState 用于 UI 显示
/// </summary>
public partial class DeviceItem : ObservableObject
{
    [ObservableProperty] private string _deviceId = string.Empty;
    [ObservableProperty] private string _status = "Unknown";
    [ObservableProperty] private DateTime _lastUpdateTime;
    [ObservableProperty] private string? _currentPosition;
}
