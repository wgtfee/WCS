namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// 面向普通测试人员的中文展示层。
/// 这里只做显示名称、参数分组和状态映射，底层仍复用既有受治理场景生成与执行流程。
/// </summary>
public partial class SimulationVerificationViewModel
{
    private static readonly IReadOnlyList<SimulationBeginnerAcceptanceTemplateItem> BeginnerAcceptanceTemplateCatalog =
    [
        new("plc-fault", "控制器测试", "控制器故障注入与恢复",
            "模拟控制器数据块读写与故障恢复，检查故障期间和恢复后的状态是否符合预期。",
            "适合测试断线、超时、读取失败、写入失败、数据卡住、位翻转、抖动和越界。"),
        new("rgv-flow", "轨道车测试", "轨道车完整搬运流程",
            "模拟轨道车装载、分配路线、分段行驶、离线恢复以及最终卸载。",
            "适合验证车辆、载荷、节点、区段、速度和电量之间的完整联动。"),
        new("traffic-lifecycle", "交通管制测试", "路权占用与释放",
            "模拟交通区域定义、占用、释放、滚动预约和过期回收。",
            "适合验证区段和交通区域的占用生命周期。"),
        new("traffic-deadlock", "交通管制测试", "交通死锁检测与解除",
            "模拟两辆轨道车交叉等待，检查系统能否识别死锁并完成解除。",
            "适合验证双车、双区段和双交通区域的冲突处理。"),
        new("external-fault", "外部接口测试", "外部接口异常与恢复",
            "模拟外部系统调用异常、故障清除和熔断恢复。",
            "适合验证接口超时、调用失败以及恢复后的再次调用。"),
        new("health-rul", "健康状态测试", "设备健康退化预测",
            "在受控虚拟时间内模拟设备健康状态变化并验证预测结果。",
            "适合验证设备健康趋势和剩余可用时间相关结果。"),
        new("integration-recovery", "综合验收", "全链路一致性恢复",
            "模拟任务从控制器、轨道车、外部接口到健康状态的完整联动和恢复。",
            "适合做跨模块的一次完整验收。"),
        new("multi-fault", "综合验收", "多故障组合恢复",
            "组合控制器故障、外部接口故障和交通死锁，验证多个异常同时发生后的恢复能力。",
            "适合做复杂异常和恢复流程验收。")
    ];

    public IReadOnlyList<SimulationBeginnerAcceptanceTemplateItem> BeginnerAcceptanceTemplates => BeginnerAcceptanceTemplateCatalog;

    private SimulationBeginnerAcceptanceTemplateItem? _selectedBeginnerAcceptanceTemplate = BeginnerAcceptanceTemplateCatalog[0];
    public SimulationBeginnerAcceptanceTemplateItem? SelectedBeginnerAcceptanceTemplate
    {
        get => _selectedBeginnerAcceptanceTemplate;
        set
        {
            if (!SetProperty(ref _selectedBeginnerAcceptanceTemplate, value))
                return;

            SelectedAcceptanceTemplate = value is null
                ? null
                : AcceptanceTemplates.FirstOrDefault(x => string.Equals(x.Id, value.Id, StringComparison.Ordinal));
            NotifyBeginnerParameterVisibility();
        }
    }

    public IReadOnlyList<SimulationBeginnerOptionItem> BeginnerPlcFaultOptions =>
        AcceptanceLibraryPlcFaultKinds.Select(x => new SimulationBeginnerOptionItem(x, TranslateFaultKind(x))).ToArray();

    public IReadOnlyList<SimulationBeginnerOptionItem> BeginnerExternalFaultOptions =>
        AcceptanceLibraryExternalFaultKinds.Select(x => new SimulationBeginnerOptionItem(x, TranslateFaultKind(x))).ToArray();

    public IReadOnlyList<SimulationBeginnerOptionItem> BeginnerExternalSystemOptions =>
        AcceptanceLibraryExternalSystemKinds.Select(x => new SimulationBeginnerOptionItem(x, TranslateSystemKind(x))).ToArray();

    private SimulationBeginnerOptionItem? _selectedBeginnerPlcFaultOption;
    public SimulationBeginnerOptionItem? SelectedBeginnerPlcFaultOption
    {
        get => _selectedBeginnerPlcFaultOption ??=
            BeginnerPlcFaultOptions.FirstOrDefault(x => string.Equals(x.Value, AcceptanceLibraryPlcFaultKind, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!SetProperty(ref _selectedBeginnerPlcFaultOption, value) || value is null)
                return;
            AcceptanceLibraryPlcFaultKind = value.Value;
        }
    }

    private SimulationBeginnerOptionItem? _selectedBeginnerExternalFaultOption;
    public SimulationBeginnerOptionItem? SelectedBeginnerExternalFaultOption
    {
        get => _selectedBeginnerExternalFaultOption ??=
            BeginnerExternalFaultOptions.FirstOrDefault(x => string.Equals(x.Value, AcceptanceLibraryExternalFaultKind, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!SetProperty(ref _selectedBeginnerExternalFaultOption, value) || value is null)
                return;
            AcceptanceLibraryExternalFaultKind = value.Value;
        }
    }

    private SimulationBeginnerOptionItem? _selectedBeginnerExternalSystemOption;
    public SimulationBeginnerOptionItem? SelectedBeginnerExternalSystemOption
    {
        get => _selectedBeginnerExternalSystemOption ??=
            BeginnerExternalSystemOptions.FirstOrDefault(x => string.Equals(x.Value, AcceptanceLibraryExternalSystemKind, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!SetProperty(ref _selectedBeginnerExternalSystemOption, value) || value is null)
                return;
            AcceptanceLibraryExternalSystemKind = value.Value;
        }
    }

    public bool BeginnerShowPlcParameters => SelectedBeginnerAcceptanceTemplate?.Id is
        "plc-fault" or "integration-recovery" or "multi-fault";

    public bool BeginnerShowRgvParameters => SelectedBeginnerAcceptanceTemplate?.Id is
        "rgv-flow" or "traffic-lifecycle" or "traffic-deadlock" or "integration-recovery" or "multi-fault";

    public bool BeginnerShowTrafficParameters => SelectedBeginnerAcceptanceTemplate?.Id is
        "traffic-lifecycle" or "traffic-deadlock" or "multi-fault";

    public bool BeginnerShowExternalParameters => SelectedBeginnerAcceptanceTemplate?.Id is
        "external-fault" or "integration-recovery" or "multi-fault";

    public bool BeginnerShowHealthParameters => SelectedBeginnerAcceptanceTemplate?.Id is
        "health-rul" or "integration-recovery";

    public string BeginnerAcceptanceResultText => AcceptanceResultText switch
    {
        "PASS" => "通过",
        "FAIL" => "失败",
        "RUNNING" => "执行中",
        _ => "尚未执行"
    };

    public string BeginnerAcceptanceResultDescription => AcceptanceResultText switch
    {
        "PASS" => "本次测试已完成，验收条件全部通过。",
        "FAIL" => "本次测试未通过，请查看完整执行详情定位失败步骤。",
        "RUNNING" => "正在执行测试并检查预期结果，请稍候。",
        _ when ScenarioActionCount > 0 => "测试数据已生成，可以点击“一键开始测试”。",
        _ => "请选择测试用例，确认参数后生成测试数据。"
    };

    partial void OnAcceptanceResultTextChanged(string value)
    {
        OnPropertyChanged(nameof(BeginnerAcceptanceResultText));
        OnPropertyChanged(nameof(BeginnerAcceptanceResultDescription));
    }

    private void NotifyBeginnerParameterVisibility()
    {
        OnPropertyChanged(nameof(BeginnerShowPlcParameters));
        OnPropertyChanged(nameof(BeginnerShowRgvParameters));
        OnPropertyChanged(nameof(BeginnerShowTrafficParameters));
        OnPropertyChanged(nameof(BeginnerShowExternalParameters));
        OnPropertyChanged(nameof(BeginnerShowHealthParameters));
    }

    private static string TranslateFaultKind(string value) => value switch
    {
        "Disconnect" => "断线",
        "Timeout" => "超时",
        "ReadFailure" => "读取失败",
        "WriteFailure" => "写入失败",
        "Stuck" => "数据卡住",
        "BitFlip" => "位翻转",
        "Jitter" => "数据抖动",
        "OutOfRange" => "数据越界",
        "HttpError" => "接口错误",
        "ConnectionFailure" => "连接失败",
        "CircuitOpen" => "熔断打开",
        _ => "其他异常"
    };

    private static string TranslateSystemKind(string value) => value switch
    {
        "Mes" => "制造执行系统",
        "Wms" => "仓储管理系统",
        "Erp" => "企业资源系统",
        "Http" => "通用接口",
        _ => "外部系统"
    };
}

public sealed record SimulationBeginnerAcceptanceTemplateItem(
    string Id,
    string Category,
    string Name,
    string Description,
    string ParameterHint);

public sealed record SimulationBeginnerOptionItem(string Value, string Name);