using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Text;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// EMS/RGV 调度中心页面模型。
/// 只读监控 + 2 秒自动轮询：车辆、执行、路权变化无需手动刷新；
/// 有变化才重建集合，避免 DataGrid 无谓抖动。
/// </summary>
public partial class TransportSchedulingViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;
    private string _lastSignature = string.Empty;

    public ObservableCollection<TransportVehicleSnapshot> Vehicles { get; } = new();
    public ObservableCollection<TransportExecutionSnapshot> Executions { get; } = new();
    public ObservableCollection<RouteReservation> Reservations { get; } = new();

    [ObservableProperty] private int _onlineVehicleCount;
    [ObservableProperty] private int _executingVehicleCount;
    [ObservableProperty] private int _waitingExecutionCount;
    [ObservableProperty] private int _reservedEdgeCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private DateTime? _lastRefreshTime;

    public TransportSchedulingViewModel(IWcsApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync()
    {
        // 调度状态高频变化：后台自动轮询，手动刷新仍可用
        StartPollingLoop(TimeSpan.FromSeconds(2), QuietRefreshAsync);
        return RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在读取调度状态...";

        try
        {
            var changed = await RefreshCoreAsync().ConfigureAwait(true);
            LastRefreshTime = DateTime.Now;
            StatusText = changed
                ? $"已刷新：{LastRefreshTime:yyyy-MM-dd HH:mm:ss}（每 2 秒自动更新）"
                : $"无变化（上次刷新 {LastRefreshTime:HH:mm:ss}）";
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

    /// <summary>静默轮询：跳过忙碌期，签名未变化时不动集合。</summary>
    private async Task QuietRefreshAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed || IsBusy)
            return;

        try
        {
            var vehiclesTask = _api.GetTransportVehiclesAsync();
            var executionsTask = _api.GetTransportExecutionsAsync();
            var reservationsTask = _api.GetTransportReservationsAsync();
            await Task.WhenAll(vehiclesTask, executionsTask, reservationsTask).ConfigureAwait(true);

            if (IsDisposed || IsBusy)
                return;

            var signature = BuildSignature(
                vehiclesTask.Result,
                executionsTask.Result,
                reservationsTask.Result);
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
                return;

            ApplyData(vehiclesTask.Result, executionsTask.Result, reservationsTask.Result);
            _lastSignature = signature;
            LastRefreshTime = DateTime.Now;
            StatusText = $"自动更新：在线 {OnlineVehicleCount} 台 · 执行中 {ExecutingVehicleCount} · 等待 {WaitingExecutionCount} · 预约边 {ReservedEdgeCount}（{LastRefreshTime:HH:mm:ss}）";
        }
        catch
        {
            // 轮询失败保持静默，不打断用户操作；手动刷新仍会报告错误
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        var vehiclesTask = _api.GetTransportVehiclesAsync();
        var executionsTask = _api.GetTransportExecutionsAsync();
        var reservationsTask = _api.GetTransportReservationsAsync();

        await Task.WhenAll(vehiclesTask, executionsTask, reservationsTask).ConfigureAwait(true);

        var signature = BuildSignature(vehiclesTask.Result, executionsTask.Result, reservationsTask.Result);
        var changed = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
        if (changed)
        {
            ApplyData(vehiclesTask.Result, executionsTask.Result, reservationsTask.Result);
            _lastSignature = signature;
        }
        return true;
    }

    private void ApplyData(
        IReadOnlyList<TransportVehicleSnapshot> vehicles,
        IReadOnlyList<TransportExecutionSnapshot> executions,
        IReadOnlyList<RouteReservation> reservations)
    {
        Replace(Vehicles, vehicles);
        Replace(Executions, executions);
        Replace(Reservations, reservations);

        OnlineVehicleCount = Vehicles.Count(x => x.IsOnline);
        ExecutingVehicleCount = Vehicles.Count(x => x.State == TransportVehicleOperatingState.Executing);
        WaitingExecutionCount = Executions.Count(x =>
            x.State is TransportExecutionState.WaitingForRoute or
                TransportExecutionState.Paused or
                TransportExecutionState.Faulted);
        ReservedEdgeCount = Reservations.Sum(x => x.EdgeIds.Count);
    }

    private static string BuildSignature(
        IEnumerable<TransportVehicleSnapshot> vehicles,
        IEnumerable<TransportExecutionSnapshot> executions,
        IEnumerable<RouteReservation> reservations)
    {
        var builder = new StringBuilder(256);
        foreach (var v in vehicles)
            builder.Append(v.VehicleId).Append(':').Append(v.State).Append(':')
                   .Append(v.CurrentNodeId).Append(':').Append(v.Version).Append(';');
        builder.Append('|');
        foreach (var e in executions)
            builder.Append(e.RequestId).Append(':').Append(e.State).Append(':')
                   .Append(e.CurrentNodeIndex).Append(':').Append(e.LastFeedbackSequence).Append(';');
        builder.Append('|');
        foreach (var r in reservations)
            builder.Append(r.ReservationId).Append(':').Append(r.EdgeIds.Count).Append(';');
        return builder.ToString();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
