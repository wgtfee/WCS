using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// EMS/RGV 调度中心页面模型。
/// 当前提供只读监控，控制操作后续按权限和现场安全流程开放。
/// </summary>
public partial class TransportSchedulingViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

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

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在读取调度状态...";

        try
        {
            var vehiclesTask = _api.GetTransportVehiclesAsync();
            var executionsTask = _api.GetTransportExecutionsAsync();
            var reservationsTask = _api.GetTransportReservationsAsync();

            await Task.WhenAll(vehiclesTask, executionsTask, reservationsTask);

            Replace(Vehicles, vehiclesTask.Result);
            Replace(Executions, executionsTask.Result);
            Replace(Reservations, reservationsTask.Result);

            OnlineVehicleCount = Vehicles.Count(x => x.IsOnline);
            ExecutingVehicleCount = Vehicles.Count(x => x.State == TransportVehicleOperatingState.Executing);
            WaitingExecutionCount = Executions.Count(x =>
                x.State is TransportExecutionState.WaitingForRoute or
                    TransportExecutionState.Paused or
                    TransportExecutionState.Faulted);
            ReservedEdgeCount = Reservations.Sum(x => x.EdgeIds.Count);

            LastRefreshTime = DateTime.Now;
            StatusText = $"已刷新：{LastRefreshTime:yyyy-MM-dd HH:mm:ss}";
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
