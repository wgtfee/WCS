using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>第八阶段现场联调工作台。危险写点和冲突处置只在审批 API 中执行。</summary>
public partial class TransportCommissioningViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<TransportSignalTemplate> Templates { get; } = new();
    public ObservableCollection<TransportFaultDefinition> Faults { get; } = new();
    public ObservableCollection<TransportRecoveryConflictCase> Conflicts { get; } = new();
    public ObservableCollection<TransportCommandCompensationItem> CompensationItems { get; } = new();
    public ObservableCollection<TransportCommunicationTrace> Traces { get; } = new();

    [ObservableProperty] private int _templateCount;
    [ObservableProperty] private int _faultDefinitionCount;
    [ObservableProperty] private int _pendingConflictCount;
    [ObservableProperty] private int _manualCompensationCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private string _probeVehicleId = string.Empty;
    [ObservableProperty] private string _probeSummary = "请输入车辆号后执行在线探测";

    public TransportCommissioningViewModel(IWcsApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取现场联调数据...";
        try
        {
            var templatesTask = _api.GetTransportSignalTemplatesAsync();
            var faultsTask = _api.GetTransportFaultDefinitionsAsync();
            var conflictsTask = _api.GetTransportRecoveryConflictsAsync();
            var compensationTask = _api.GetTransportCommandCompensationAsync();
            var tracesTask = _api.GetTransportCommunicationTracesAsync(500);
            await Task.WhenAll(templatesTask, faultsTask, conflictsTask, compensationTask, tracesTask);

            Replace(Templates, templatesTask.Result);
            Replace(Faults, faultsTask.Result);
            Replace(Conflicts, conflictsTask.Result);
            Replace(CompensationItems, compensationTask.Result?.Items ?? Array.Empty<TransportCommandCompensationItem>());
            Replace(Traces, tracesTask.Result);
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
    private async Task RefreshConflictsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在重新比对数据库与 PLC 状态...";
        try
        {
            Replace(Conflicts, await _api.RefreshTransportRecoveryConflictsAsync());
            UpdateCounters();
            StatusText = $"冲突清单已刷新，待处置 {PendingConflictCount} 项";
        }
        catch (Exception ex)
        {
            StatusText = $"冲突刷新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(ProbeVehicleId))
        {
            ProbeSummary = "车辆号不能为空";
            return;
        }

        IsBusy = true;
        ProbeSummary = $"正在探测 {ProbeVehicleId.Trim()}...";
        try
        {
            var result = await _api.ProbeTransportVehicleAsync(ProbeVehicleId.Trim());
            if (result is null)
            {
                ProbeSummary = "探测接口未返回结果";
                return;
            }
            ProbeSummary = result.Connected
                ? $"连接正常；读取 {result.Values.Count} 个点位；耗时 {result.DurationMs:0.0} ms"
                : $"连接失败：{result.Error}";
        }
        catch (Exception ex)
        {
            ProbeSummary = $"探测失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateCounters()
    {
        TemplateCount = Templates.Count;
        FaultDefinitionCount = Faults.Count(x => x.Enabled);
        PendingConflictCount = Conflicts.Count(x => x.State == TransportRecoveryConflictState.Pending);
        ManualCompensationCount = CompensationItems.Count(x =>
            x.Decision == TransportCommandCompensationDecision.RequiresManualConfirmation);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
