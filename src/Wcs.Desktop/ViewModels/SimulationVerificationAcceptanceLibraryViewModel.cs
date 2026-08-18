namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>
/// Product-facing scenario library for the existing governed S0-S10 simulator.
/// The library deliberately reuses the already-verified S2/S3/S4/S5/S6/integration
/// scenario builders and the Batch C timeline composer instead of defining another DSL.
/// </summary>
public partial class SimulationVerificationViewModel
{
    private static readonly SimulationAcceptanceTemplateItem[] AcceptanceTemplateCatalog =
    [
        new("plc-fault", "PLC", "PLC 故障注入与恢复",
            "DB Block define/write/read + plc.fault.apply/clear，并验证故障窗口与恢复。",
            "重点参数：PLC、Fault Kind、故障持续时间。支持现有 S2 的 8 类 PLC Fault。"),
        new("rgv-flow", "RGV", "RGV 完整搬运流程",
            "Segment/Vehicle define → Load → Route → Advance → Offline/Online → Advance → Unload。",
            "重点参数：Vehicle、Load、Source/Middle/Destination、Segment、速度、电量。"),
        new("traffic-lifecycle", "Traffic", "路权生命周期",
            "Zone define → reserve/release → rolling reserve/release → expire。",
            "重点参数：Vehicle、Segment、Zone、Lease、速度。"),
        new("traffic-deadlock", "Traffic", "死锁检测与解除",
            "双车交叉等待 → deadlock.detect → deadlock.resolve，并验证等待关系与解除结果。",
            "重点参数：Vehicle A/B、Segment A/B、Zone A/B、Lease。"),
        new("external-fault", "External", "外部接口异常恢复",
            "Endpoint define → fault.apply → request.invoke → fault.clear → circuit.reset。",
            "重点参数：Endpoint、System Kind、Fault Kind、Operation、故障持续时间。"),
        new("health-rul", "Health", "Health / RUL 退化",
            "受控虚拟时间内执行 Health profile、forecast oracle 与 outcome，并验证 RUL/Probability 契约。",
            "重点参数：Asset、虚拟退化时长。"),
        new("integration-recovery", "Integration", "全链 Mission 一致性恢复",
            "Mission define/dispatch/advance/ack，覆盖 PLC Block、RGV、External、Health 与 exactly-once 一致性。",
            "重点参数：Mission 关联的 PLC、Vehicle、Load、节点、Endpoint、Asset。"),
        new("multi-fault", "综合", "PLC + External + Traffic 多故障恢复",
            "把现有 PLC Fault、External Fault、Traffic Deadlock 三个真实 Scenario 通过 Batch C 时间轴合并为一个受治理场景。",
            "重点参数：PLC Fault、External Fault、双车/区段；所有子场景仍使用已有生成器。")
    ];

    public IReadOnlyList<SimulationAcceptanceTemplateItem> AcceptanceTemplates => AcceptanceTemplateCatalog;
    public IReadOnlyList<string> AcceptanceLibraryPlcFaultKinds => SupportedPlcFaultKinds;
    public IReadOnlyList<string> AcceptanceLibraryExternalFaultKinds => SupportedExternalFaultKinds;
    public IReadOnlyList<string> AcceptanceLibraryExternalSystemKinds => SupportedExternalSystemKinds;

    [ObservableProperty] private SimulationAcceptanceTemplateItem? _selectedAcceptanceTemplate = AcceptanceTemplateCatalog[0];
    [ObservableProperty] private string _acceptanceLibraryStatusText = "选择场景模板，调整少量参数后即可生成严格 Scenario DSL 或直接一键验收。";
    [ObservableProperty] private string _acceptanceLibraryScenarioVersion = "1.0.0";
    [ObservableProperty] private string _acceptanceLibrarySeedText = "20260811";
    [ObservableProperty] private string _acceptanceLibraryStartUtcText = "2026-08-11T00:00:00+00:00";
    [ObservableProperty] private string _acceptanceLibrarySpeedFactorText = "1";

    [ObservableProperty] private string _acceptanceLibraryPlcId = "PLC1";
    [ObservableProperty] private string _acceptanceLibraryPlcFaultKind = "BitFlip";
    [ObservableProperty] private string _acceptanceLibraryFaultDurationMsText = "50";

    [ObservableProperty] private string _acceptanceLibraryVehicleId = "RGV1";
    [ObservableProperty] private string _acceptanceLibraryVehicleBId = "RGV2";
    [ObservableProperty] private string _acceptanceLibraryLoadId = "LOAD1";
    [ObservableProperty] private string _acceptanceLibrarySourceNodeId = "N1";
    [ObservableProperty] private string _acceptanceLibraryMiddleNodeId = "N2";
    [ObservableProperty] private string _acceptanceLibraryDestinationNodeId = "N3";
    [ObservableProperty] private string _acceptanceLibrarySegmentA = "S1";
    [ObservableProperty] private string _acceptanceLibrarySegmentB = "S2";
    [ObservableProperty] private string _acceptanceLibraryZoneA = "Z1";
    [ObservableProperty] private string _acceptanceLibraryZoneB = "Z2";
    [ObservableProperty] private string _acceptanceLibrarySegmentLengthMmText = "1000";
    [ObservableProperty] private string _acceptanceLibraryVehicleSpeedMmPerSecondText = "1000";
    [ObservableProperty] private string _acceptanceLibraryBatteryPercentText = "100";
    [ObservableProperty] private string _acceptanceLibraryLeaseMsText = "10000";

    [ObservableProperty] private string _acceptanceLibraryExternalEndpointId = "MES1";
    [ObservableProperty] private string _acceptanceLibraryExternalSystemKind = "Mes";
    [ObservableProperty] private string _acceptanceLibraryExternalFaultKind = "Timeout";
    [ObservableProperty] private string _acceptanceLibraryExternalOperation = "Order.Push";

    [ObservableProperty] private string _acceptanceLibraryHealthAssetId = "ASSET1";
    [ObservableProperty] private string _acceptanceLibraryHealthDurationHoursText = "72";

    [RelayCommand]
    private void GenerateAcceptanceLibraryScenario() => TryBuildAcceptanceLibraryScenario();

    [RelayCommand]
    private async Task GenerateAndRunAcceptanceLibraryScenarioAsync()
    {
        if (TryBuildAcceptanceLibraryScenario())
            await RunOneClickAcceptanceAsync().ConfigureAwait(true);
    }

    private bool TryBuildAcceptanceLibraryScenario()
    {
        if (SelectedAcceptanceTemplate is null)
            return AcceptanceLibraryError("请先选择一个验收场景模板。");
        if (!TryLibraryVersion(AcceptanceLibraryScenarioVersion, out var version))
            return false;
        if (!long.TryParse(AcceptanceLibrarySeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) || seed == 0)
            return AcceptanceLibraryError("Seed 必须是非 0 Int64。");
        if (!DateTimeOffset.TryParse(AcceptanceLibraryStartUtcText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            return AcceptanceLibraryError("StartTimeUtc 格式无效。");
        if (!double.TryParse(AcceptanceLibrarySpeedFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speedFactor) ||
            !double.IsFinite(speedFactor) || speedFactor <= 0)
            return AcceptanceLibraryError("Speed Factor 必须是大于 0 的有限数字。");
        if (!long.TryParse(AcceptanceLibraryFaultDurationMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var faultDuration) || faultDuration < 1)
            return AcceptanceLibraryError("故障持续时间必须是大于 0 的整数毫秒。");

        VisualScenarioSeedText = seed.ToString(CultureInfo.InvariantCulture);
        VisualScenarioStartUtcText = AcceptanceLibraryStartUtcText.Trim();

        var built = SelectedAcceptanceTemplate.Id switch
        {
            "plc-fault" => TryBuildLibraryPlc(faultDuration),
            "rgv-flow" => TryBuildLibraryRgv(),
            "traffic-lifecycle" => TryBuildLibraryTraffic(deadlock: false),
            "traffic-deadlock" => TryBuildLibraryTraffic(deadlock: true),
            "external-fault" => TryBuildLibraryExternal(faultDuration),
            "health-rul" => TryBuildLibraryHealth(),
            "integration-recovery" => TryBuildLibraryIntegrationRecovery(),
            "multi-fault" => TryBuildLibraryMultiFault(faultDuration),
            _ => AcceptanceLibraryError($"未知模板：{SelectedAcceptanceTemplate.Id}")
        };

        if (!built)
        {
            AcceptanceLibraryStatusText = StatusText;
            return false;
        }

        if (!ApplyAcceptanceLibraryVersion(version))
            return false;

        ScenarioSource = "Wcs.Desktop Simulation Acceptance Center";
        ScenarioApprovedBy = "simulation-operator";
        SpeedFactorText = speedFactor.ToString(CultureInfo.InvariantCulture);
        AcceptanceLibraryStatusText =
            $"已生成：{SelectedAcceptanceTemplate.Name} · {ScenarioId}@{ScenarioVersion}。下一步可直接一键验收，仍走 S0 SHA-256 + S1 Run 隔离。";
        StatusText = AcceptanceLibraryStatusText;
        return true;
    }

    private bool TryBuildLibraryPlc(long faultDuration)
    {
        if (!TryLibraryRequired(AcceptanceLibraryPlcId, "PLC", out var plcId))
            return false;
        var faultStart = 30L;
        var faultEnd = checked(faultStart + faultDuration);
        DevicePlcBlockKey = $"{plcId}.DB100";
        DevicePlcFaultId = $"F-{Slug(plcId).ToUpperInvariant()}-LIB";
        DevicePlcFaultKind = AcceptanceLibraryPlcFaultKind;
        DevicePlcFaultStartMsText = faultStart.ToString(CultureInfo.InvariantCulture);
        DevicePlcFaultEndMsText = faultEnd.ToString(CultureInfo.InvariantCulture);
        return TryLoadDevicePlcScenario();
    }

    private bool TryBuildLibraryRgv()
    {
        ApplyLibraryRgvInputs();
        return TryLoadDeviceRgvScenario();
    }

    private bool TryBuildLibraryTraffic(bool deadlock)
    {
        ApplyLibraryTrafficInputs();
        return deadlock ? TryLoadTrafficDeadlockScenario() : TryLoadTrafficOperationsScenario();
    }

    private bool TryBuildLibraryExternal(long faultDuration)
    {
        if (!TryLibraryRequired(AcceptanceLibraryExternalEndpointId, "External Endpoint", out var endpointId))
            return false;
        ExternalEndpointId = endpointId;
        ExternalSystemKind = AcceptanceLibraryExternalSystemKind;
        ExternalFaultKind = AcceptanceLibraryExternalFaultKind;
        ExternalFaultId = $"F-{Slug(endpointId).ToUpperInvariant()}-LIB";
        ExternalFaultStartMsText = "10";
        ExternalFaultEndMsText = checked(10L + faultDuration).ToString(CultureInfo.InvariantCulture);
        ExternalOperation = AcceptanceLibraryExternalOperation;
        return TryLoadExternalOperationsScenario();
    }

    private bool TryBuildLibraryHealth()
    {
        VisualHealthAssetId = AcceptanceLibraryHealthAssetId;
        VisualHealthDurationHoursText = AcceptanceLibraryHealthDurationHoursText;
        return TryLoadVisualHealthScenario();
    }

    private bool TryBuildLibraryIntegrationRecovery()
    {
        if (!TryLibraryRequired(AcceptanceLibraryVehicleId, "Vehicle", out var vehicleId))
            return false;
        VisualRecoveryMissionId = $"M-{Slug(vehicleId).ToUpperInvariant()}-LIB";
        VisualRecoveryPlcBlockKey = $"{AcceptanceLibraryPlcId.Trim()}.DB100";
        VisualRecoveryVehicleId = vehicleId;
        VisualRecoveryLoadId = AcceptanceLibraryLoadId;
        VisualRecoverySourceNodeId = AcceptanceLibrarySourceNodeId;
        VisualRecoveryMiddleNodeId = AcceptanceLibraryMiddleNodeId;
        VisualRecoveryDestinationNodeId = AcceptanceLibraryDestinationNodeId;
        VisualRecoveryExternalEndpointId = AcceptanceLibraryExternalEndpointId;
        VisualRecoveryHealthAssetId = AcceptanceLibraryHealthAssetId;
        return TryLoadVisualRecoveryScenario();
    }

    private bool TryBuildLibraryMultiFault(long faultDuration)
    {
        TimelineItems.Clear();
        SelectedTimelineItem = null;

        if (!TryBuildLibraryPlc(faultDuration))
            return false;
        TimelineAppendOffsetMsText = "0";
        ImportCurrentScenario(replace: true);
        if (TimelineItems.Count == 0)
            return AcceptanceLibraryError("PLC 子场景没有成功进入时间轴。");

        var afterPlc = TimelineItems.Count;
        if (!TryBuildLibraryExternal(faultDuration))
            return false;
        TimelineAppendOffsetMsText = "500";
        ImportCurrentScenario(replace: false);
        if (TimelineItems.Count <= afterPlc)
            return AcceptanceLibraryError("External 子场景没有成功进入时间轴。");

        var afterExternal = TimelineItems.Count;
        ApplyLibraryTrafficInputs();
        if (!TryLoadTrafficDeadlockScenario())
            return false;
        TimelineAppendOffsetMsText = "1000";
        ImportCurrentScenario(replace: false);
        if (TimelineItems.Count <= afterExternal)
            return AcceptanceLibraryError("Traffic Deadlock 子场景没有成功进入时间轴。");

        ScenarioId = $"acceptance-multifault-{Slug(AcceptanceLibraryPlcId)}-{Slug(AcceptanceLibraryExternalEndpointId)}";
        ScenarioVersion = "1.0.0";
        ScenarioFile = $"{ScenarioId}.json";
        ScenarioSource = "Wcs.Desktop Simulation Acceptance Center";
        ScenarioApprovedBy = "simulation-operator";
        return TryGenerateTimelineScenario();
    }

    private void ApplyLibraryRgvInputs()
    {
        DeviceRgvVehicleId = AcceptanceLibraryVehicleId;
        DeviceRgvSourceNodeId = AcceptanceLibrarySourceNodeId;
        DeviceRgvMiddleNodeId = AcceptanceLibraryMiddleNodeId;
        DeviceRgvDestinationNodeId = AcceptanceLibraryDestinationNodeId;
        DeviceRgvSegmentA = AcceptanceLibrarySegmentA;
        DeviceRgvSegmentB = AcceptanceLibrarySegmentB;
        DeviceRgvSegmentLengthMmText = AcceptanceLibrarySegmentLengthMmText;
        DeviceRgvSpeedMmPerSecondText = AcceptanceLibraryVehicleSpeedMmPerSecondText;
        DeviceRgvBatteryPercentText = AcceptanceLibraryBatteryPercentText;
        DeviceRgvLoadId = AcceptanceLibraryLoadId;
    }

    private void ApplyLibraryTrafficInputs()
    {
        TrafficVehicleId = AcceptanceLibraryVehicleId;
        TrafficVehicleBId = AcceptanceLibraryVehicleBId;
        TrafficSourceNodeId = AcceptanceLibrarySourceNodeId;
        TrafficMiddleNodeId = AcceptanceLibraryMiddleNodeId;
        TrafficDestinationNodeId = AcceptanceLibraryDestinationNodeId;
        TrafficSegmentIdsText = $"{AcceptanceLibrarySegmentA},{AcceptanceLibrarySegmentB}";
        TrafficZoneId = AcceptanceLibraryZoneA;
        TrafficZoneBId = AcceptanceLibraryZoneB;
        TrafficSegmentLengthMmText = AcceptanceLibrarySegmentLengthMmText;
        TrafficVehicleSpeedMmPerSecondText = AcceptanceLibraryVehicleSpeedMmPerSecondText;
        TrafficLeaseMsText = AcceptanceLibraryLeaseMsText;
        TrafficDeadlockDetectAtMsText = "60";
    }

    private bool ApplyAcceptanceLibraryVersion(string version)
    {
        try
        {
            var node = JsonNode.Parse(ScenarioJson ?? string.Empty) as JsonObject
                ?? throw new InvalidOperationException("Scenario JSON 根节点必须是对象。");
            node["Version"] = version;
            ScenarioVersion = version;
            ScenarioFile = $"{ScenarioId}-{version}.json";
            ScenarioJson = node.ToJsonString(VisualScenarioJsonOptions);
            return true;
        }
        catch (Exception exception)
        {
            return AcceptanceLibraryError($"应用 Scenario Version 失败：{exception.Message}");
        }
    }

    private bool TryLibraryVersion(string? raw, out string version)
    {
        version = raw?.Trim() ?? string.Empty;
        var parts = version.Split('.');
        if (parts.Length == 3 && parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return true;
        return AcceptanceLibraryError("Scenario Version 必须使用 major.minor.patch，例如 1.0.0。");
    }

    private bool TryLibraryRequired(string? raw, string field, out string value)
    {
        value = raw?.Trim() ?? string.Empty;
        return value.Length > 0 || AcceptanceLibraryError($"{field} 不能为空。");
    }

    private bool AcceptanceLibraryError(string message)
    {
        AcceptanceLibraryStatusText = message;
        StatusText = message;
        return false;
    }
}

public sealed record SimulationAcceptanceTemplateItem(
    string Id,
    string Category,
    string Name,
    string Description,
    string ParameterHint);
