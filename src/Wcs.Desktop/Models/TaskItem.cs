using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Models;

/// <summary>
/// 任务列表项 - 对应后端 TaskContext 全部字段
/// </summary>
public partial class TaskItem : ObservableObject
{
    [ObservableProperty] private string _taskId = string.Empty;
    [ObservableProperty] private string _deviceId = string.Empty;
    [ObservableProperty] private string _status = "Created";
    [ObservableProperty] private int _priority;
    [ObservableProperty] private string _priorityLevel = "Normal";
    [ObservableProperty] private string _category = "Production";
    [ObservableProperty] private string _routeId = string.Empty;
    [ObservableProperty] private DateTime _createdTime;
    [ObservableProperty] private DateTime? _startTime;
    [ObservableProperty] private DateTime? _endTime;
    [ObservableProperty] private int _retryCount;
    [ObservableProperty] private int _maxRetries = 3;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _parentTaskId;
    [ObservableProperty] private string? _dependencies;
}
