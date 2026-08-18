namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;
using Wcs.Optimization;

public partial class DigitalTwinOptimizerViewModel : ViewModelBase
{
    private readonly IDigitalTwinOptimizerApiService _api;

    public ObservableCollection<OptimizationExperimentSummary> Experiments { get; } = [];
    public ObservableCollection<OptimizationPolicyScore> Ranking { get; } = [];

    [ObservableProperty] private string _environment = "Unknown";
    [ObservableProperty] private string _modeText = "未连接";
    [ObservableProperty] private string _recoveryText = "未知";
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private string _experimentId = string.Empty;
    [ObservableProperty] private string _resultText = "未加载实验结果";
    [ObservableProperty] private string _evidenceHash = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public string SafetyText => "IDI-P5 仅用于 Digital Twin 仿真实验、Candidate 对比、Pareto/多目标排名和 Evidence 查看；最大自动化等级 L1，不提供实验执行 API，不自动替换生产调度策略，不写 PLC/CommandBus/TaskScheduler/TaskOrchestrator/DeviceManager/Dispatch/Traffic/RouteReservation 控制链路。";

    public DigitalTwinOptimizerViewModel(IDigitalTwinOptimizerApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取 IDI-P5 状态和实验 Evidence...";
        try
        {
            var status = await _api.GetStatusAsync().ConfigureAwait(true);
            if (status is null)
            {
                ClearUnavailable();
                return;
            }

            Environment = status.Environment;
            ModeText = $"{status.Mode} / {status.MaximumAutomationLevel} / ControlWrite={status.ControlWriteAllowed} / AutoReplace={status.AutoProductionPolicyReplacementAllowed}";
            RecoveryText = $"Definitions {status.Recovery.DefinitionCount} · Results {status.Recovery.CompletedResultCount} · Invalid {status.Recovery.InvalidDefinitionCount} · Healthy {status.Recovery.Healthy}";

            var values = await _api.ListExperimentsAsync(100).ConfigureAwait(true);
            Experiments.Clear();
            foreach (var item in values) Experiments.Add(item);
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · Experiments {Experiments.Count} · Determinism rounds/input {status.DeterminismRoundsPerInput}";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败（保持 fail-closed）：{ex.Message}";
            RecoveryText = "Fail-closed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadResultAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(ExperimentId)) return;
        IsBusy = true;
        Ranking.Clear();
        EvidenceHash = string.Empty;
        ResultText = "正在读取实验结果...";
        try
        {
            var value = await _api.GetResultAsync(ExperimentId.Trim()).ConfigureAwait(true);
            if (value is null)
            {
                ResultText = "未找到已完成实验结果";
                return;
            }

            foreach (var item in value.Ranking) Ranking.Add(item);
            EvidenceHash = value.EvidenceHash;
            ResultText = $"Runs {value.Runs.Count} · Candidates {value.Ranking.Count} · SoftwareHead {value.SoftwareHead} · ControlWrite={value.ControlWriteAllowed} · ProductionAutomation={value.ProductionAutomationAllowed}";
        }
        catch (Exception ex)
        {
            ResultText = $"查询失败（fail-closed）：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearUnavailable()
    {
        Environment = "Unavailable";
        ModeText = "安全拒绝";
        RecoveryText = "Fail-closed";
        Experiments.Clear();
        Ranking.Clear();
        ResultText = "当前环境未开放 IDI-P5，或 Production fail-closed 已拒绝访问。";
        StatusText = ResultText;
    }
}
