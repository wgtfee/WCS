using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 仪表盘 ViewModel - 系统概览卡片
///
/// 实时消息只做"合并刷新请求"：最多同时一次在途 HTTP 刷新，
/// 在途期间到达的任意多条消息合并为完成后的一次补刷，
/// 避免每条广播触发一次完整 /api/overview 请求的风暴。
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private readonly IWcsRealtimeService _realtime;

    /// <summary>是否有刷新在途。</summary>
    private volatile bool _refreshInFlight;
    /// <summary>在途期间又收到新信号，完成后需要补刷一次。</summary>
    private volatile bool _refreshPending;

    [ObservableProperty] private int _deviceCount;
    [ObservableProperty] private int _activeTaskCount;
    [ObservableProperty] private int _activeAlarmCount;
    [ObservableProperty] private int _trackedObjectCount;
    [ObservableProperty] private int _activeLockCount;

    public DashboardViewModel(IWcsApiService api, IWcsRealtimeService realtime)
    {
        _api = api;
        _realtime = realtime;

        _realtime.DeviceStateBroadcast += OnRealtimeSignal;
        _realtime.TaskStateChanged += OnRealtimeSignal;
        _realtime.AlarmEvent += OnRealtimeSignal;
    }

    private void OnRealtimeSignal(object? _)
    {
        if (IsDisposed) return;

        if (_refreshInFlight)
        {
            _refreshPending = true;
            return;
        }

        _refreshInFlight = true;
        var ignored = RefreshCountsAsync();
    }

    protected override async Task OnInitializeAsync()
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

    private async Task RefreshCountsAsync()
    {
        try
        {
            do
            {
                _refreshPending = false;
                var overview = await _api.GetOverviewAsync();
                if (overview is not null && !IsDisposed)
                {
                    DeviceCount = overview.DeviceCount;
                    ActiveTaskCount = overview.ActiveTaskCount;
                    ActiveAlarmCount = overview.ActiveAlarmCount;
                    TrackedObjectCount = overview.TrackedObjectCount;
                    ActiveLockCount = overview.ActiveLockCount;
                }
            }
            while (_refreshPending);
        }
        catch
        {
            // 网络异常时静默忽略，等待下一次实时信号重试
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    protected override void OnDispose()
    {
        _realtime.DeviceStateBroadcast -= OnRealtimeSignal;
        _realtime.TaskStateChanged -= OnRealtimeSignal;
        _realtime.AlarmEvent -= OnRealtimeSignal;
        base.OnDispose();
    }
}
