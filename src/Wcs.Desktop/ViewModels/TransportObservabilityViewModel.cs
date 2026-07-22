using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

public partial class TransportObservabilityViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<TransportHealthComponent> HealthComponents { get; } = new();
    public ObservableCollection<TransportConsistencyIssue> ConsistencyIssues { get; } = new();
    public ObservableCollection<TransportTraceRecord> Traces { get; } = new();
    public ObservableCollection<TransportOperationMetric> OperationMetrics { get; } = new();
    public ObservableCollection<TransportConfigurationSnapshot> ConfigurationSnapshots { get; } = new();

    [ObservableProperty] private string _healthState = "Unknown";
    [ObservableProperty] private int _healthScore;
    [ObservableProperty] private int _onlineVehicleCount;
    [ObservableProperty] private int _offlineVehicleCount;
    [ObservableProperty] private int _activeExecutionCount;
    [ObservableProperty] private int _queueLength;
    [ObservableProperty] private int _activeReservationCount;
    [ObservableProperty] private int _activeAlarmCount;
    [ObservableProperty] private int _consistencyIssueCount;
    [ObservableProperty] private long _totalConsistencyIssueCount;
    [ObservableProperty] private double _lastQueueWaitMilliseconds;
    [ObservableProperty] private double _lastPlcResponseMilliseconds;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportObservabilityViewModel(IWcsApiService api)
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
        StatusText = "正在读取可观测性状态...";
        try
        {
            var summaryTask = _api.GetTransportObservabilityAsync();
            var tracesTask = _api.GetTransportTracesAsync(500);
            var snapshotsTask = _api.GetTransportConfigurationSnapshotsAsync(100);
            await Task.WhenAll(summaryTask, tracesTask, snapshotsTask);

            ApplySummary(summaryTask.Result ?? new TransportObservabilitySnapshot());
            Replace(Traces, tracesTask.Result);
            Replace(ConfigurationSnapshots, snapshotsTask.Result);
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
    private async Task EvaluateHealthAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = "正在重新计算运输健康评分...";
        try
        {
            var health = await _api.EvaluateTransportHealthAsync();
            if (health is not null)
            {
                HealthState = health.State.ToString();
                HealthScore = health.Score;
                Replace(HealthComponents, health.Components);
            }
            StatusText = $"健康评估完成：{HealthState} / {HealthScore}";
        }
        catch (Exception ex)
        {
            StatusText = $"健康评估失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task InspectConsistencyAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = "正在执行数据库、内存与 PLC 三方巡检...";
        try
        {
            var report = await _api.InspectTransportConsistencyAsync();
            if (report is not null)
            {
                Replace(ConsistencyIssues, report.Issues);
                ConsistencyIssueCount = report.Issues.Count;
                StatusText = report.IsConsistent
                    ? "三方状态一致"
                    : $"巡检完成：发现 {report.Issues.Count} 项差异";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"一致性巡检失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    private void ApplySummary(TransportObservabilitySnapshot summary)
    {
        HealthState = summary.Health.State.ToString();
        HealthScore = summary.Health.Score;
        OnlineVehicleCount = summary.OnlineVehicleCount;
        OfflineVehicleCount = summary.OfflineVehicleCount;
        ActiveExecutionCount = summary.ActiveExecutionCount;
        QueueLength = summary.QueueLength;
        ActiveReservationCount = summary.ActiveReservationCount;
        ActiveAlarmCount = summary.ActiveAlarmCount;
        ConsistencyIssueCount = summary.LastConsistencyReport?.Issues.Count ?? 0;
        TotalConsistencyIssueCount = summary.Metrics.ConsistencyIssueCount;
        LastQueueWaitMilliseconds = summary.Metrics.LastQueueWaitMilliseconds;
        LastPlcResponseMilliseconds = summary.Metrics.LastPlcResponseMilliseconds;
        Replace(HealthComponents, summary.Health.Components);
        Replace(ConsistencyIssues, summary.LastConsistencyReport?.Issues ?? Array.Empty<TransportConsistencyIssue>());
        Replace(OperationMetrics, summary.Metrics.Operations);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
