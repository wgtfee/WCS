using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 任务管理 ViewModel — 全部从数据库加载，支持按列搜索过滤
/// </summary>
public partial class TasksViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private List<TaskItem> _allTasks = new();

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

    public TasksViewModel(IWcsApiService api)
    {
        _api = api;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSearchFieldChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Tasks.Clear();
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
            ApplyFilter();
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
            return;

        try
        {
            var result = await _api.CreateTaskAsync(NewTaskDeviceId, NewTaskRouteId, NewTaskPriority);
            if (result is not null)
            {
                Tasks.Add(new TaskItem
                {
                    TaskId = result.TaskId,
                    Status = result.Status.ToString(),
                    Priority = result.Priority,
                    RouteId = result.RouteId
                });
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task CancelTaskAsync(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        try
        {
            await _api.CancelTaskAsync(taskId);
            var item = Tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (item is not null) Tasks.Remove(item);
        }
        catch { }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync();
}
