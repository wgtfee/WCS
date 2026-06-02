using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Models;

/// <summary>
/// 任务列表项 - 包装 TaskRuntime 用于 UI 显示
/// </summary>
public partial class TaskItem : ObservableObject
{
    [ObservableProperty] private string _taskId = string.Empty;
    [ObservableProperty] private string _status = "Created";
    [ObservableProperty] private int _priority;
    [ObservableProperty] private string _routeId = string.Empty;
    [ObservableProperty] private DateTime? _startTime;
    [ObservableProperty] private DateTime? _endTime;
}
