using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

public partial class TransportSimulationViewModel : ViewModelBase
{
    private readonly ITransportSimulationApiService _api;

    public ObservableCollection<TransportSimulationRun> Runs { get; } = new();
    public ObservableCollection<TransportStrategyComparisonReport> Comparisons { get; } = new();
    public ObservableCollection<TransportBatchOptimizationResult> Optimizations { get; } = new();
    public ObservableCollection<TransportCapacityBenchmarkReport> Benchmarks { get; } = new();
    public ObservableCollection<TransportFinalAcceptanceReport> AcceptanceReports { get; } = new();
    public ObservableCollection<TransportCongestionForecastPoint> Forecast { get; } = new();

    [ObservableProperty] private string _latestPolicy = "无";
    [ObservableProperty] private double _latestThroughput;
    [ObservableProperty] private double _latestP95Waiting;
    [ObservableProperty] private double _latestDeadlineMissRate;
    [ObservableProperty] private double _latestFleetUtilization;
    [ObservableProperty] private int _maximumSustainableRate;
    [ObservableProperty] private int _recommendedVehicleCount;
    [ObservableProperty] private string _acceptanceState = "NotGenerated";
    [ObservableProperty] private int _runCount;
    [ObservableProperty] private int _comparisonCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportSimulationViewModel(ITransportSimulationApiService api)
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
        StatusText = "正在读取离线仿真与最终验收结果...";
        try
        {
            var summaryTask = _api.GetSummaryAsync();
            var runsTask = _api.GetRunsAsync(100);
            var comparisonsTask = _api.GetComparisonsAsync(100);
            var optimizationsTask = _api.GetOptimizationsAsync(100);
            var benchmarksTask = _api.GetBenchmarksAsync(100);
            var acceptanceTask = _api.GetAcceptanceReportsAsync(100);
            await Task.WhenAll(
                summaryTask,
                runsTask,
                comparisonsTask,
                optimizationsTask,
                benchmarksTask,
                acceptanceTask);

            var summary = summaryTask.Result ?? new TransportSimulationSummary();
            ApplySummary(summary);
            Replace(Runs, runsTask.Result);
            Replace(Comparisons, comparisonsTask.Result);
            Replace(Optimizations, optimizationsTask.Result);
            Replace(Benchmarks, benchmarksTask.Result);
            Replace(AcceptanceReports, acceptanceTask.Result);
            Replace(Forecast, summary.LatestRun?.CongestionForecast ?? Array.Empty<TransportCongestionForecastPoint>());
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

    private void ApplySummary(TransportSimulationSummary summary)
    {
        RunCount = summary.RunCount;
        ComparisonCount = summary.ComparisonCount;
        LatestPolicy = summary.LatestRun?.PolicyName ?? "无";
        LatestThroughput = summary.LatestRun?.Metrics.ThroughputPerHour ?? 0;
        LatestP95Waiting = summary.LatestRun?.Metrics.P95WaitingSeconds ?? 0;
        LatestDeadlineMissRate = summary.LatestRun?.Metrics.DeadlineMissRatePercent ?? 0;
        LatestFleetUtilization = summary.LatestRun?.Metrics.FleetUtilizationPercent ?? 0;
        MaximumSustainableRate = summary.LatestBenchmark?.MaximumSustainableTaskRatePerHour ?? 0;
        RecommendedVehicleCount = summary.LatestBenchmark?.RecommendedVehicleCount ?? 0;
        AcceptanceState = summary.LatestAcceptance?.State.ToString() ?? "NotGenerated";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
