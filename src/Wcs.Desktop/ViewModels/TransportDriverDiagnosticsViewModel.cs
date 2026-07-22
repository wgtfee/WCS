using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>第七阶段 PLC 驱动诊断页面。默认只开放读取、轮询和安全对账。</summary>
public partial class TransportDriverDiagnosticsViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<TransportPlcSignalMap> Maps { get; } = new();
    public ObservableCollection<TransportDriverDiagnosticSnapshot> Diagnostics { get; } = new();
    public ObservableCollection<TransportDriverReconciliationItem> ReconciliationItems { get; } = new();

    [ObservableProperty] private int _mapCount;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _faultCount;
    [ObservableProperty] private int _manualConfirmationCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportDriverDiagnosticsViewModel(IWcsApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取 PLC 驱动状态...";
        try
        {
            var mapsTask = _api.GetTransportPlcSignalMapsAsync();
            var diagnosticsTask = _api.GetTransportDriverDiagnosticsAsync();
            await Task.WhenAll(mapsTask, diagnosticsTask);
            Replace(Maps, mapsTask.Result);
            Replace(Diagnostics, diagnosticsTask.Result);
            UpdateCounters();
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
    private async Task PollAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在执行一次 PLC 批量轮询...";
        try
        {
            var report = await _api.PollTransportDriversAsync();
            await RefreshCoreAsync();
            StatusText = report is null
                ? "轮询完成"
                : $"轮询完成：更新 {report.UpdatedCount}，离线 {report.OfflineCount}，故障 {report.FaultedCount}";
        }
        catch (Exception ex)
        {
            StatusText = $"轮询失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReconcileAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在对比数据库与 PLC 实际状态...";
        try
        {
            var report = await _api.ReconcileTransportDriversAsync();
            Replace(ReconciliationItems, report?.Items ?? Array.Empty<TransportDriverReconciliationItem>());
            ManualConfirmationCount = report?.ManualConfirmationCount ?? 0;
            StatusText = report is null
                ? "对账完成"
                : $"对账完成：一致 {report.InSyncCount}，需人工确认 {report.ManualConfirmationCount}";
        }
        catch (Exception ex)
        {
            StatusText = $"对账失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        Replace(Diagnostics, await _api.GetTransportDriverDiagnosticsAsync());
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        MapCount = Maps.Count;
        OnlineCount = Diagnostics.Count(x => x.DeviceOnline);
        FaultCount = Diagnostics.Count(x => x.FaultCode != 0 || x.OperatingState == TransportVehicleOperatingState.Faulted);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
