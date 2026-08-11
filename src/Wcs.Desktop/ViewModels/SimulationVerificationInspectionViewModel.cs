namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;

public partial class SimulationVerificationViewModel
{
    private ISimulationInspectionApiService? _inspectionApi;

    public ObservableCollection<SimulationInspectionItemDto> InspectionItems { get; } = [];

    [ObservableProperty] private string _inspectionTitle = "请选择 S2～S8 阶段和一个非终态 Run";
    [ObservableProperty] private string _inspectionStatusText = "尚未读取分层状态";

    public SimulationVerificationViewModel(
        ISimulationVerificationApiService api,
        ISimulationInspectionApiService inspectionApi)
        : this(api)
    {
        _inspectionApi = inspectionApi;
    }

    [RelayCommand]
    private Task InspectStatusAsync() => InspectStageAsync(SimulationInspectionView.Status, "状态");

    [RelayCommand]
    private Task InspectPrimaryAsync() => InspectStageAsync(SimulationInspectionView.Primary, "主要对象");

    [RelayCommand]
    private Task InspectSecondaryAsync() => InspectStageAsync(SimulationInspectionView.Secondary, "异常/占用");

    [RelayCommand]
    private Task InspectAuditAsync() => InspectStageAsync(SimulationInspectionView.Audit, "审计");

    private async Task InspectStageAsync(SimulationInspectionView view, string viewText)
    {
        if (_inspectionApi is null)
        {
            StatusText = "分层检查服务未注册。";
            return;
        }
        if (!CanExecuteSimulation || IsBusy)
            return;
        if (SelectedStage is null)
        {
            StatusText = "请先在统一总览选择 S2～S8 阶段。";
            return;
        }

        var stageId = SelectedStage.Id.ToUpperInvariant();
        if (stageId is not ("S2" or "S3" or "S4" or "S5" or "S6" or "S7" or "S8"))
        {
            StatusText = "分层检查仅支持 S2～S8。S0/S1 使用场景治理与 Run 控制；S9 真实 HIL 继续保持独立只读边界。";
            return;
        }

        Guid? runId = null;
        if (stageId != "S8")
        {
            if (SelectedRun is null)
            {
                StatusText = $"{stageId} 检查需要先选择一个 Run。";
                return;
            }
            if (SelectedRun.IsTerminal)
            {
                StatusText = $"{stageId} 的现有检查 API 基于 S1 Checkpoint，只允许非终态 Run。请在 Run 完成前读取；终态使用 Evidence Hash/Final State Hash。";
                return;
            }
            runId = SelectedRun.RunId;
        }

        IsBusy = true;
        InspectionTitle = $"{stageId} {SelectedStage.Name} · {viewText}";
        InspectionStatusText = "正在读取只读 Simulation State...";
        try
        {
            var items = await _inspectionApi
                .GetStageInspectionAsync(stageId, view, runId)
                .ConfigureAwait(true);

            InspectionItems.Clear();
            foreach (var item in items)
                InspectionItems.Add(item);

            InspectionStatusText = $"已读取 {InspectionItems.Count} 个字段；数据来自现有 {SelectedStage.ApiPrefix} GET inspection。";
            StatusText = $"{InspectionTitle} 已刷新。";
        }
        catch (Exception exception)
        {
            InspectionItems.Clear();
            InspectionStatusText = $"读取失败：{exception.Message}";
            StatusText = InspectionStatusText;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
