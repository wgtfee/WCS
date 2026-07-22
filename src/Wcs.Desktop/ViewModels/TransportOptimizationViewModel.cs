using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 第五阶段充电、任务转移和运行效率监控页面。
/// 故障任务转移入口仍由 Host API 暴露，Desktop 默认只读，避免误操作现场任务。
/// </summary>
public partial class TransportOptimizationViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<TransportChargingStationSnapshot> Stations { get; } = new();
    public ObservableCollection<TransportChargingPlan> ChargingPlans { get; } = new();
    public ObservableCollection<TransportTaskReassignmentRecord> Reassignments { get; } = new();
    public ObservableCollection<TransportVehiclePerformanceSnapshot> VehicleMetrics { get; } = new();

    [ObservableProperty] private int _onlineVehicleCount;
    [ObservableProperty] private int _chargingVehicleCount;
    [ObservableProperty] private int _lowBatteryVehicleCount;
    [ObservableProperty] private int _waitingTaskCount;
    [ObservableProperty] private int _completedTaskCount;
    [ObservableProperty] private int _reassignmentCount;
    [ObservableProperty] private double _fleetUtilizationPercent;
    [ObservableProperty] private double _completionRatePercent;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportOptimizationViewModel(IWcsApiService api)
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
        StatusText = "正在读取充电与效率状态...";

        try
        {
            var stationsTask = _api.GetTransportChargingStationsAsync();
            var plansTask = _api.GetTransportChargingPlansAsync();
            var reassignmentsTask = _api.GetTransportReassignmentsAsync();
            var performanceTask = _api.GetTransportPerformanceAsync();

            await Task.WhenAll(
                stationsTask,
                plansTask,
                reassignmentsTask,
                performanceTask);

            Replace(Stations, stationsTask.Result);
            Replace(ChargingPlans, plansTask.Result);
            Replace(Reassignments, reassignmentsTask.Result);

            var performance = performanceTask.Result ?? new TransportPerformanceSnapshot();
            Replace(VehicleMetrics, performance.Vehicles);
            OnlineVehicleCount = performance.OnlineVehicleCount;
            ChargingVehicleCount = performance.ChargingVehicleCount;
            LowBatteryVehicleCount = performance.LowBatteryVehicleCount;
            WaitingTaskCount = performance.WaitingTaskCount;
            CompletedTaskCount = performance.CompletedTaskCount;
            ReassignmentCount = performance.ReassignmentCount;
            FleetUtilizationPercent = performance.FleetUtilizationPercent;
            CompletionRatePercent = performance.CompletionRatePercent;

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
    private async Task EvaluateChargingAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在执行低电量车辆评估...";

        try
        {
            var evaluations = await _api.EvaluateTransportChargingAsync();
            var created = evaluations.Count(x => x.PlanCreated);
            var critical = evaluations.Count(x => x.IsCritical);
            StatusText = $"评估完成：新计划 {created}，临界电量 {critical}";
        }
        catch (Exception ex)
        {
            StatusText = $"评估失败：{ex.Message}";
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
