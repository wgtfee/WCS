using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 任务管理 ViewModel — 全部从数据库加载
/// </summary>
public partial class TasksViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private readonly IWcsRealtimeService _realtime;

    public ObservableCollection<TaskItem> Tasks { get; } = new();

    [ObservableProperty] private string _newTaskDeviceId = string.Empty;
    [ObservableProperty] private string _newTaskRouteId = string.Empty;
    [ObservableProperty] private int _newTaskPriority = 2;
    [ObservableProperty] private bool _isLoading;

    public TasksViewModel(IWcsApiService api, IWcsRealtimeService realtime)
    {
        _api = api;
        _realtime = realtime;

        _realtime.TaskStateChanged += msg =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var existing = Tasks.FirstOrDefault(t => t.TaskId == msg.TaskId);
                if (existing is not null)
                {
                    existing.Status = msg.Runtime.Status.ToString();
                    existing.StartTime = msg.Runtime.StartTime;
                    existing.EndTime = msg.Runtime.EndTime;
                }
            });
        };
    }

    protected override async Task OnInitializeAsync()
    {
        await LoadAsync();
    }

    /// <summary>
    /// 从数据库加载持久化的任务运行记录（Wcs_TaskRun 表）
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var tasks = await _api.GetTasksFromDbAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tasks.Clear();
                foreach (var t in tasks)
                {
                    Tasks.Add(new TaskItem
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
                    });
                }
            });
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
