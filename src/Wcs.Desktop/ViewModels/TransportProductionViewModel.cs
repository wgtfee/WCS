using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 第九阶段生产调度看板。只开放刷新、试算、立即调度和安全故障接管评估；
/// 参数、站点和单轨定义修改仍必须通过 Host 审批接口。
/// </summary>
public partial class TransportProductionViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<TransportProductionQueueItem> Queue { get; } = new();
    public ObservableCollection<TransportStationRuntimeSnapshot> Stations { get; } = new();
    public ObservableCollection<TransportSingleTrackSectionSnapshot> SingleTracks { get; } = new();
    public ObservableCollection<TransportDispatchDecisionFrame> Decisions { get; } = new();
    public ObservableCollection<TransportProductionTrendPoint> TrendPoints { get; } = new();
    public ObservableCollection<TransportFaultTakeoverItem> Takeovers { get; } = new();
    public ObservableCollection<TransportProductionDryRunItem> DryRunItems { get; } = new();

    [ObservableProperty] private long _tuningVersion;
    [ObservableProperty] private int _maximumDispatchPerCycle;
    [ObservableProperty] private int _queuedTaskCount;
    [ObservableProperty] private int _waitingForStationCount;
    [ObservableProperty] private int _waitingForTrafficCount;
    [ObservableProperty] private int _activeSingleTrackPermitCount;
    [ObservableProperty] private int _singleTrackWaitingCount;
    [ObservableProperty] private double _maximumStationUtilizationPercent;
    [ObservableProperty] private double _averageFleetUtilizationPercent;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportProductionViewModel(IWcsApiService api)
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
        StatusText = "正在读取生产调度状态...";
        try
        {
            var tuningTask = _api.GetTransportProductionTuningAsync();
            var stationsTask = _api.GetTransportProductionStationsAsync();
            var tracksTask = _api.GetTransportSingleTrackAsync();
            var queueTask = _api.GetTransportProductionQueueAsync();
            var decisionsTask = _api.GetTransportDispatchDecisionsAsync(200);
            var trendsTask = _api.GetTransportProductionTrendsAsync(DateTime.UtcNow.AddHours(-24), DateTime.UtcNow);

            await Task.WhenAll(tuningTask, stationsTask, tracksTask, queueTask, decisionsTask, trendsTask);

            var tuning = tuningTask.Result ?? new TransportProductionTuningOptions();
            TuningVersion = tuning.Version;
            MaximumDispatchPerCycle = tuning.MaximumDispatchPerCycle;
            Replace(Stations, stationsTask.Result);
            Replace(SingleTracks, tracksTask.Result);
            Replace(Queue, queueTask.Result);
            Replace(Decisions, decisionsTask.Result);

            var trends = trendsTask.Result ?? new TransportProductionTrendSummary();
            Replace(TrendPoints, trends.Points);
            AverageFleetUtilizationPercent = trends.AverageFleetUtilizationPercent;

            QueuedTaskCount = Queue.Count(x => x.State is not TransportProductionQueueState.Assigned);
            WaitingForStationCount = Queue.Count(x => x.State == TransportProductionQueueState.WaitingForStation);
            WaitingForTrafficCount = Queue.Count(x => x.State == TransportProductionQueueState.WaitingForTraffic);
            ActiveSingleTrackPermitCount = SingleTracks.Sum(x => x.ActivePermits.Count);
            SingleTrackWaitingCount = SingleTracks.Sum(x => x.WaitingRequests.Count);
            MaximumStationUtilizationPercent = Stations.Count == 0 ? 0 : Stations.Max(x => x.UtilizationPercent);
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

    [RelayCommand]
    private async Task DispatchCycleAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = "正在执行一次生产派单竞争...";
        try
        {
            var result = await _api.RunTransportProductionDispatchCycleAsync();
            StatusText = result is null
                ? "派单接口未返回结果"
                : $"派单完成：竞争 {result.ConsideredCount}，成功 {result.AssignedCount}，等待 {result.WaitingCount}";
        }
        catch (Exception ex)
        {
            StatusText = $"派单失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = "正在执行无副作用调度试算...";
        try
        {
            var report = await _api.GetTransportProductionDryRunAsync();
            Replace(DryRunItems, report?.Items ?? Array.Empty<TransportProductionDryRunItem>());
            StatusText = $"试算完成：{DryRunItems.Count} 个任务参与排序，不会写 PLC 或占用路权";
        }
        catch (Exception ex)
        {
            StatusText = $"试算失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EvaluateTakeoverAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = "正在执行故障车辆安全接管评估...";
        try
        {
            var report = await _api.EvaluateTransportFaultTakeoverAsync();
            Replace(Takeovers, report?.Items ?? Array.Empty<TransportFaultTakeoverItem>());
            StatusText = $"接管评估完成：{Takeovers.Count} 项";
        }
        catch (Exception ex)
        {
            StatusText = $"接管评估失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
