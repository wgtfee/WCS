using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.StateCenter.Models;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 任务管理 ViewModel — 全部从数据库加载，支持按列搜索过滤。
///
/// 反馈说明：数据库快照最长滞后一个持久化周期（默认 10 秒），
/// 因此任务状态流转通过 SignalR 实时事件原地更新行数据，
/// 不再依赖手动刷新；新建/取消等写操作仍走 API。
/// </summary>
public partial class TasksViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private readonly IWcsRealtimeService _realtime;
    private List<TaskItem> _allTasks = new();
    private readonly Action<TaskStateChangedMessage> _onTaskStateChanged;

    public ObservableCollection<TaskItem> Tasks { get; } = new();
    public List<string> SearchFields { get; } = new()
    {
        "全部", "任务 ID", "设备", "状态", "路径", "错误信息"
    };

    [ObservableProperty] private string _newTaskDeviceId = string.Empty;
    [ObservableProperty] private string _newTaskRouteId = string.Empty;
    [ObservableProperty] private int _newTaskPriority = 2;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _searchField = "全部";
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TasksViewModel(IWcsApiService api, IWcsRealtimeService realtime)
    {
        _api = api;
        _realtime = realtime;

        // 实时状态流转：Host 端 TaskStateManager 在任务状态变化时推送，
        // 此处按 TaskId 原地更新（TaskItem 为 ObservableObject，行会自动刷新）。
        _onTaskStateChanged = msg => ApplyRealtimeUpdate(msg);
        _realtime.TaskStateChanged += _onTaskStateChanged;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSearchFieldChanged(string value) => ApplyFilter();

    private void ApplyRealtimeUpdate(TaskStateChangedMessage msg)
    {
        if (IsDisposed || msg.Runtime is null)
            return;

        var item = Tasks.FirstOrDefault(t => t.TaskId == msg.TaskId)
                   ?? _allTasks.FirstOrDefault(t => t.TaskId == msg.TaskId);
        if (item is null)
            return;

        item.Status = msg.Runtime.Status.ToString();
        item.StartTime = msg.Runtime.StartTime ?? item.StartTime;
        item.EndTime = msg.Runtime.EndTime ?? item.EndTime;
    }

    private void ApplyFilter()
    {
        var items = string.IsNullOrWhiteSpace(SearchText)
            ? _allTasks
            : _allTasks.Where(t => SearchField switch
            {
                "任务 ID" => t.TaskId.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "设备" => t.DeviceId.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "状态" => t.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "路径" => t.RouteId.Contains(SearchText, StringComparison.OrdinalIgnoreCase),
                "错误信息" => t.ErrorMessage?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false,
                _ => t.TaskId.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     t.DeviceId.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     t.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     t.RouteId.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                     (t.ErrorMessage?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
            });

        Tasks.Clear();
        foreach (var item in items)
            Tasks.Add(item);
    }

    protected override async Task OnInitializeAsync()
    {
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var tasks = await _api.GetTasksFromDbAsync();
            _allTasks = tasks.Select(t => new TaskItem
            {
                TaskId = t.TaskId,
                DeviceId = t.DeviceId,
                Status = t.Status.ToString(),
                Priority = t.Priority,
                RouteId = t.RouteId,
                CreatedTime = t.CreatedTime,
                StartTime = t.StartTime,
                EndTime = t.EndTime,
                RetryCount = t.RetryCount,
                ErrorMessage = t.ErrorMessage
            }).ToList();

            StatusText = $"已加载 {_allTasks.Count} 条任务（状态变化实时更新，无需手动刷新）";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task CreateTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTaskDeviceId) ||
            string.IsNullOrWhiteSpace(NewTaskRouteId))
        {
            StatusText = "请填写设备 ID 和路径。";
            return;
        }

        try
        {
            var result = await _api.CreateTaskAsync(NewTaskDeviceId, NewTaskRouteId, NewTaskPriority);
            if (result is not null)
            {
                var item = new TaskItem
                {
                    TaskId = result.TaskId,
                    Status = result.Status.ToString(),
                    Priority = result.Priority,
                    RouteId = result.RouteId
                };
                Tasks.Insert(0, item);
                _allTasks.Insert(0, item);
                StatusText = $"任务 {result.TaskId} 已创建，后续状态将实时更新。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"创建失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task CancelTaskAsync(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        try
        {
            await _api.CancelTaskAsync(taskId);
            StatusText = $"取消请求已提交：{taskId}（终态以实时状态为准）";
        }
        catch (Exception ex)
        {
            StatusText = $"取消失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync();

    protected override void OnDispose()
    {
        _realtime.TaskStateChanged -= _onTaskStateChanged;
        base.OnDispose();
    }
}
