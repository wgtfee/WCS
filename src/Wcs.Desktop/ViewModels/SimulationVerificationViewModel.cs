namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;

public partial class SimulationVerificationViewModel : ViewModelBase
{
    private readonly ISimulationVerificationApiService _api;

    public ObservableCollection<SimulationVerificationStageDto> Stages { get; } = [];

    [ObservableProperty] private SimulationVerificationStageDto? _selectedStage;
    [ObservableProperty] private string _environment = "Unknown";
    [ObservableProperty] private string _modeText = "未连接";
    [ObservableProperty] private string _simulationText = "未知";
    [ObservableProperty] private string _hilText = "未知";
    [ObservableProperty] private string _realHilText = "Pending";
    [ObservableProperty] private string _safetyText = "页面仅允许只读检查";
    [ObservableProperty] private int _availableStageCount;
    [ObservableProperty] private int _totalStageCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public SimulationVerificationViewModel(ISimulationVerificationApiService api)
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
        StatusText = "正在读取 S0～S10 统一验证状态...";
        try
        {
            var overview = await _api.GetOverviewAsync().ConfigureAwait(true);
            if (overview is null)
            {
                ClearUnavailable();
                StatusText = "当前 Host 环境未启用统一验证只读页面，或安全边界已拒绝访问。";
                return;
            }

            Apply(overview);
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception exception)
        {
            ClearUnavailable();
            StatusText = $"读取失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(SimulationVerificationOverviewDto overview)
    {
        Environment = overview.Environment;
        ModeText = overview.ReadOnly && !overview.RemoteControlAllowed ? "只读安全模式" : "配置异常";
        SimulationText = overview.SimulationInspectionAvailable ? "可用" : "当前环境不可用";
        HilText = overview.HilInspectionAvailable ? "可用" : "当前环境不可用";
        RealHilText = overview.RealHilExecuted && overview.ProtocolValidated &&
                      overview.MechanicalSafetyAccepted && overview.SiteAccepted
            ? "Accepted"
            : "Pending";
        SafetyText = overview.RealHilEvidenceRequiredForCompletion
            ? "真实 HIL、协议、机械安全和现场验收必须使用外部真实证据"
            : "配置异常：缺少真实证据要求";

        Stages.Clear();
        foreach (var stage in overview.Stages.OrderBy(x => ParseStageNumber(x.Id)))
            Stages.Add(stage);

        TotalStageCount = Stages.Count;
        AvailableStageCount = Stages.Count(x => x.Availability == "Available");
        SelectedStage = Stages.FirstOrDefault();
    }

    private void ClearUnavailable()
    {
        Environment = "Unavailable";
        ModeText = "安全拒绝";
        SimulationText = "不可用";
        HilText = "不可用";
        RealHilText = "Pending";
        SafetyText = "没有开放任何控制入口";
        AvailableStageCount = 0;
        TotalStageCount = 0;
        SelectedStage = null;
        Stages.Clear();
    }

    private static int ParseStageNumber(string value) =>
        value.Length > 1 && int.TryParse(value[1..], out var number) ? number : int.MaxValue;
}
