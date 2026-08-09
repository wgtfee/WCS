namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;
using Wcs.IndustrialIntelligence.Governance;

public partial class BoundedAutomationReadinessViewModel : ViewModelBase
{
    private readonly IBoundedAutomationReadinessApiService _api;

    public ObservableCollection<BoundedAutomationReadinessEvidenceRecord> Evidence { get; } = [];
    public ObservableCollection<string> PermanentProhibitions { get; } = [];

    [ObservableProperty] private string _environment = "Unknown";
    [ObservableProperty] private string _modeText = "未连接";
    [ObservableProperty] private string _claimText = "software-side ready only";
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private string _evaluationId = string.Empty;
    [ObservableProperty] private string _selectedEvidenceText = "未加载 Evidence";
    [ObservableProperty] private bool _isBusy;

    public string SafetyText => "IDI-P6 只评估 Bounded Automation 的软件侧就绪条件；默认 Disabled。没有真实 Site/HIL/Safety/Rollback Evidence 时 L2/L3 不满足就绪条件；即使 Evidence 齐全，本阶段仍不授予 Production enablement，不提供执行、审批、KillSwitch 操作、Rollback 执行或任何 PLC/调度控制写入口。";

    public BoundedAutomationReadinessViewModel(IBoundedAutomationReadinessApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取 IDI-P6 只读治理状态和 Evidence...";
        try
        {
            var status = await _api.GetStatusAsync().ConfigureAwait(true);
            if (status is null)
            {
                ClearUnavailable();
                return;
            }

            Environment = status.Environment;
            ClaimText = status.FinalClaim;
            ModeText = $"{status.Mode} / HostMax={status.HostMaximumAutomationLevel} / DefaultsDisabled={status.DefaultsDisabled} / Production={status.ProductionEnablementAllowed} / ControlWrite={status.ControlWriteAllowed}";

            var prohibitions = await _api.GetProhibitionsAsync().ConfigureAwait(true);
            PermanentProhibitions.Clear();
            foreach (var item in prohibitions) PermanentProhibitions.Add(item);

            var evidence = await _api.ListEvidenceAsync(100).ConfigureAwait(true);
            Evidence.Clear();
            foreach (var item in evidence) Evidence.Add(item);

            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · Evidence {Evidence.Count} · Permanent prohibitions {PermanentProhibitions.Count}";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败（保持 fail-closed）：{ex.Message}";
            ModeText = "Fail-closed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadEvidenceAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(EvaluationId)) return;
        IsBusy = true;
        SelectedEvidenceText = "正在读取 Evidence...";
        try
        {
            var value = await _api.GetEvidenceAsync(EvaluationId).ConfigureAwait(true);
            SelectedEvidenceText = value is null
                ? "未找到该 EvaluationId 的 Evidence"
                : $"{value.EvaluationId} · Level={value.RequestedLevel} · SoftwareReady={value.SoftwareSideReady} · Production={value.ProductionEnablementAllowed} · Head={value.SoftwareHeadSha} · DecisionHash={value.DecisionHash} · Claim={value.Claim}";
        }
        catch (Exception ex)
        {
            SelectedEvidenceText = $"查询失败（fail-closed）：{ex.Message}";
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
        ClaimText = "software-side ready only";
        Evidence.Clear();
        PermanentProhibitions.Clear();
        SelectedEvidenceText = "当前环境未开放 IDI-P6，或 Production fail-closed 已拒绝访问。";
        StatusText = SelectedEvidenceText;
    }
}
