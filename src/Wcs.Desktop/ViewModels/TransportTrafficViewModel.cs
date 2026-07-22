using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 第四阶段交通控制监控页面。危险的强制放行和资源释放操作不在本页面开放。
/// </summary>
public partial class TransportTrafficViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<TransportTrafficResourceDefinition> Resources { get; } = new();
    public ObservableCollection<TransportTrafficHold> Holds { get; } = new();
    public ObservableCollection<TransportTrafficWait> Waits { get; } = new();
    public ObservableCollection<TransportDeadlockCycle> Deadlocks { get; } = new();
    public ObservableCollection<TransportTrafficIncident> Incidents { get; } = new();

    [ObservableProperty] private int _resourceCount;
    [ObservableProperty] private int _occupiedResourceCount;
    [ObservableProperty] private int _waitingTaskCount;
    [ObservableProperty] private int _deadlockCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportTrafficViewModel(IWcsApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在读取交通控制状态...";
        try
        {
            var snapshotTask = _api.GetTransportTrafficAsync();
            var deadlocksTask = _api.GetTransportDeadlocksAsync();
            await Task.WhenAll(snapshotTask, deadlocksTask);

            var snapshot = snapshotTask.Result ?? new TransportTrafficSnapshot();
            Replace(Resources, snapshot.Resources);
            Replace(Holds, snapshot.Holds);
            Replace(Waits, snapshot.Waits);
            Replace(Incidents, snapshot.Incidents);
            Replace(Deadlocks, deadlocksTask.Result);

            ResourceCount = Resources.Count;
            OccupiedResourceCount = Holds.Select(x => x.ResourceId).Distinct(StringComparer.Ordinal).Count();
            WaitingTaskCount = Waits.Count;
            DeadlockCount = Deadlocks.Count;
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
