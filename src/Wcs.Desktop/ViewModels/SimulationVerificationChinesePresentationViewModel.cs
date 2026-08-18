namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 统一仿真中心的中文展示属性。
/// 这里只转换界面显示，不改变环境值、场景协议、运行状态或安全边界判定。
/// </summary>
public partial class SimulationVerificationViewModel
{
    public string EnvironmentDisplayText => Environment switch
    {
        "Production" => "生产环境",
        "Simulation" => "仿真环境",
        "SimulationLoadTest" => "仿真压测环境",
        "Unavailable" => "当前环境不可用",
        "Unknown" => "环境状态未知",
        _ => "受控环境"
    };

    public string RealHilDisplayText => RealHilText switch
    {
        "Accepted" => "已验收",
        "Pending" => "待验收",
        _ => "状态未知"
    };

    public string ExecutionBoundaryDisplayText => CanExecuteSimulation
        ? "仅操作软件仿真状态；生产环境、真实控制器、真实轨道车和真实硬件在环始终不在本控制面内。"
        : "当前环境未开放仿真执行，只允许查看已授权信息。";

    public string SimulationPageStatusDisplayText => IsBusy
        ? "正在处理仿真验证请求，请稍候。"
        : CanExecuteSimulation
            ? "仿真环境已就绪，可以执行受治理的场景测试。"
            : "当前环境仅允许查看，不开放仿真执行。";

    partial void OnEnvironmentChanged(string value)
    {
        OnPropertyChanged(nameof(EnvironmentDisplayText));
    }

    partial void OnRealHilTextChanged(string value)
    {
        OnPropertyChanged(nameof(RealHilDisplayText));
    }

    partial void OnCanExecuteSimulationChanged(bool value)
    {
        OnPropertyChanged(nameof(ExecutionBoundaryDisplayText));
        OnPropertyChanged(nameof(SimulationPageStatusDisplayText));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(SimulationPageStatusDisplayText));
    }
}