using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 仪表盘 ViewModel - 系统概览卡片
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private readonly IWcsRealtimeService _realtime;

    [ObservableProperty] private int _deviceCount;
    [ObservableProperty] private int _activeTaskCount;
    [ObservableProperty] private int _activeAlarmCount;
    [ObservableProperty] private int _trackedObjectCount;
    [ObservableProperty] private int _activeLockCount;

    public DashboardViewModel(IWcsApiService api, IWcsRealtimeService realtime)
    {
        _api = api;
        _realtime = realtime;

        _realtime.DeviceStateBroadcast += _ => RefreshCounts();
        _realtime.TaskStateChanged += _ => RefreshCounts();
        _realtime.AlarmEvent += _ => RefreshCounts();
    }

    public async Task InitializeAsync()
    {
        await LoadAsync();
    }
    public async Task LoadAsync()
    {
        try
        {
            var overview = await _api.GetOverviewAsync();
            if (overview is null) return;
            DeviceCount = overview.DeviceCount;
            ActiveTaskCount = overview.ActiveTaskCount;
            ActiveAlarmCount = overview.ActiveAlarmCount;
            TrackedObjectCount = overview.TrackedObjectCount;
            ActiveLockCount = overview.ActiveLockCount;
        }
        catch
        {
            // Ignore on initial load
        }
    }

    private async void RefreshCounts()
    {
        try
        {
            var overview = await _api.GetOverviewAsync();
            if (overview is null) return;
            DeviceCount = overview.DeviceCount;
            ActiveTaskCount = overview.ActiveTaskCount;
            ActiveAlarmCount = overview.ActiveAlarmCount;
            TrackedObjectCount = overview.TrackedObjectCount;
            ActiveLockCount = overview.ActiveLockCount;
        }
        catch { }
    }
}
