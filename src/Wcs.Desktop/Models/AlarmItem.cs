using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Models;

/// <summary>
/// 报警列表项 - 包装 AlarmState 用于 UI 显示
/// </summary>
public partial class AlarmItem : ObservableObject
{
    [ObservableProperty] private string _alarmId = string.Empty;
    [ObservableProperty] private string _alarmCode = string.Empty;
    [ObservableProperty] private string _status = "Active";
    [ObservableProperty] private string _level = "Info";
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private DateTime _occurTime;
    [ObservableProperty] private DateTime? _recoverTime;
}
