namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;
using Wcs.ModelOps;

public partial class ModelOpsViewModel : ViewModelBase
{
    private readonly IModelOpsApiService _api;

    public ObservableCollection<AiModelDeployment> Deployments { get; } = [];
    public ObservableCollection<AiModelAuditEntry> AuditEntries { get; } = [];

    [ObservableProperty] private string _modelId = "asset-health";
    [ObservableProperty] private string _assetType = "RGV";
    [ObservableProperty] private string _profile = "default";
    [ObservableProperty] private string _modelVersion = string.Empty;
    [ObservableProperty] private string _actor = "operator";
    [ObservableProperty] private string _reason = "ModelOps approval";
    [ObservableProperty] private AiModelDeployment? _selectedDeployment;
    [ObservableProperty] private string _environment = "Unknown";
    [ObservableProperty] private string _modeText = "未连接";
    [ObservableProperty] private string _recoveryText = "未知";
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private bool _isBusy;

    public string SafetyText => "IDI-P1 仅管理模型版本、Shadow/Champion/Fallback/Quarantine 与审计；不写 PLC、不调用调度控制链路、不自动提升模型。";

    public ModelOpsViewModel(IModelOpsApiService api)
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
        StatusText = "正在读取 ModelOps 状态...";
        try
        {
            var status = await _api.GetStatusAsync().ConfigureAwait(true);
            if (status is null)
            {
                ClearUnavailable();
                return;
            }

            Environment = status.Environment;
            ModeText = $"{status.Mode} / L{status.MaximumAutomationLevel.TrimStart('L')}";
            RecoveryText = status.RecoveryHealthy
                ? $"Healthy · Champion {status.ChampionCount} · Fallback {status.FallbackCount} · Shadow {status.ShadowCount}"
                : "Fail-closed: " + string.Join(" | ", status.RecoveryErrors);

            var deployments = await _api.GetDeploymentsAsync(ModelId, AssetType, Profile).ConfigureAwait(true);
            Deployments.Clear();
            foreach (var item in deployments)
                Deployments.Add(item);

            var audit = await _api.GetAuditAsync(ModelId, 100).ConfigureAwait(true);
            AuditEntries.Clear();
            foreach (var item in audit)
                AuditEntries.Add(item);

            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败：{ex.Message}";
            RecoveryText = "Fail-closed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnterShadowAsync()
    {
        if (!CanOperate(ModelVersion))
            return;
        await ExecuteAsync(
            "进入 Shadow",
            ct => _api.PromoteShadowAsync(DeploymentRequest(ModelVersion), ct));
    }

    [RelayCommand]
    private async Task PromoteChampionAsync()
    {
        var version = SelectedDeployment?.ModelVersion ?? ModelVersion;
        if (!CanOperate(version))
            return;
        await ExecuteAsync(
            "批准 Champion",
            ct => _api.PromoteChampionAsync(DeploymentRequest(version), ct));
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (!CanOperate(ModelId))
            return;
        var request = new ModelRollbackRequest(
            ModelId.Trim(),
            AssetType.Trim(),
            Profile.Trim(),
            Actor.Trim(),
            Reason.Trim(),
            Correlation("rollback"));
        await ExecuteAsync("回滚到 Fallback", ct => _api.RollbackAsync(request, ct));
    }

    [RelayCommand]
    private async Task QuarantineAsync()
    {
        var version = SelectedDeployment?.ModelVersion ?? ModelVersion;
        if (!CanOperate(version))
            return;
        var request = new ModelQuarantineRequest(
            ModelId.Trim(),
            version.Trim(),
            AssetType.Trim(),
            Profile.Trim(),
            Actor.Trim(),
            Reason.Trim(),
            Correlation("quarantine"));
        await ExecuteAsync("隔离模型版本", ct => _api.QuarantineAsync(request, ct));
    }

    private async Task ExecuteAsync(string action, Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = $"{action}...";
        try
        {
            await operation(CancellationToken.None).ConfigureAwait(true);
            StatusText = $"{action}成功；正在刷新 Evidence...";
        }
        catch (Exception ex)
        {
            StatusText = $"{action}失败（保持 fail-closed）：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    private ModelDeploymentRequest DeploymentRequest(string version) =>
        new(
            ModelId.Trim(),
            version.Trim(),
            AssetType.Trim(),
            Profile.Trim(),
            Actor.Trim(),
            Reason.Trim(),
            Correlation("deployment"));

    private bool CanOperate(string? version)
    {
        if (string.IsNullOrWhiteSpace(ModelId) || string.IsNullOrWhiteSpace(AssetType) ||
            string.IsNullOrWhiteSpace(Profile) || string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(Actor) || string.IsNullOrWhiteSpace(Reason))
        {
            StatusText = "ModelId、AssetType、Profile、Version、Actor、Reason 均不能为空。";
            return false;
        }
        return true;
    }

    private static string Correlation(string action) =>
        $"desktop-{action}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";

    private void ClearUnavailable()
    {
        Environment = "Unavailable";
        ModeText = "安全拒绝";
        RecoveryText = "Fail-closed";
        Deployments.Clear();
        AuditEntries.Clear();
        StatusText = "当前环境未开放 IDI-P1 ModelOps，或 Production fail-closed 已拒绝访问。";
    }
}
