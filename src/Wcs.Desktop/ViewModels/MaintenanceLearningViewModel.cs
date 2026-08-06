namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;
using Wcs.MaintenanceLearning;

public partial class MaintenanceLearningViewModel : ViewModelBase
{
    private readonly IMaintenanceLearningApiService _api;

    public ObservableCollection<MesOutboxEntry> PendingOutbox { get; } = [];

    [ObservableProperty] private string _environment = "Unknown";
    [ObservableProperty] private string _modeText = "未连接";
    [ObservableProperty] private string _recoveryText = "未知";
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private string _interventionId = string.Empty;
    [ObservableProperty] private string _interventionText = "未查询";
    [ObservableProperty] private bool _isBusy;

    public string SafetyText => "IDI-P4 仅用于维修结果学习、效果评估、标签审批和 Evidence；不自动训练、不自动激活模型、不写 PLC/调度控制链路。";

    public MaintenanceLearningViewModel(IMaintenanceLearningApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取 Maintenance Learning 状态...";
        try
        {
            var status = await _api.GetStatusAsync().ConfigureAwait(true);
            if (status is null)
            {
                ClearUnavailable();
                return;
            }

            Environment = status.Environment;
            ModeText = $"{status.Mode} / {status.MaximumAutomationLevel}";
            RecoveryText = $"Interventions {status.Recovery.InterventionCount} · Pending Outbox {status.Recovery.PendingOutboxCount} · Pending Labels {status.Recovery.PendingLabelCount}";

            var outbox = await _api.GetPendingOutboxAsync(100).ConfigureAwait(true);
            PendingOutbox.Clear();
            foreach (var item in outbox) PendingOutbox.Add(item);
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
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
    private async Task FindInterventionAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(InterventionId)) return;
        IsBusy = true;
        try
        {
            var value = await _api.GetInterventionAsync(InterventionId.Trim()).ConfigureAwait(true);
            InterventionText = value is null
                ? "未找到"
                : $"{value.AssetId} / {value.AssetType} / {value.ActionType} / {value.CompletedAt:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            InterventionText = $"查询失败：{ex.Message}";
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
        PendingOutbox.Clear();
        StatusText = "当前环境未开放 IDI-P4 Maintenance Learning，或 Production fail-closed 已拒绝访问。";
    }
}
