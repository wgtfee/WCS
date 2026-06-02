using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Models;

/// <summary>
/// 物体追踪列表项 - 包装 ObjectState 用于 UI 显示
/// </summary>
public partial class ObjectItem : ObservableObject
{
    [ObservableProperty] private string _objectId = string.Empty;
    [ObservableProperty] private string _currentPosition = string.Empty;
    [ObservableProperty] private string? _targetPosition;
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private DateTime _updateTime;
}
