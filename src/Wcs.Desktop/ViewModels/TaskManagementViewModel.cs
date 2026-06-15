using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 任务管理 ViewModel
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
            var existing = Tasks.FirstOrDefault(t => t.TaskId == msg.TaskId);
            if (existing is not null)
            {
                existing.Status = msg.Runtime.Status.ToString();
                existing.StartTime = msg.Runtime.StartTime;
                existing.EndTime = msg.Runtime.EndTime;
            }
        };
    }

    public async Task InitializeAsync()
    {
        await LoadAsync();
    }
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var tasks = await _api.GetActiveTasksAsync();
            Tasks.Clear();
            foreach (var t in tasks)
            {
                Tasks.Add(new TaskItem
                {
                    TaskId = t.TaskId,
                    Status = t.Status.ToString(),
                    Priority = t.Priority,
                    RouteId = t.RouteId,
                    StartTime = t.StartTime,
                    EndTime = t.EndTime
                });
            }
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
