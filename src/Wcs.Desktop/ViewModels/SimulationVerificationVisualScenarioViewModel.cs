namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using System.Text;
using System.Text.Json;

public partial class SimulationVerificationViewModel
{
    private static readonly JsonSerializerOptions VisualScenarioJsonOptions = new()
    {
        WriteIndented = true
    };

    [ObservableProperty] private string _visualScenarioSeedText = "20260811";
    [ObservableProperty] private string _visualScenarioStartUtcText = "2026-08-11T00:00:00+00:00";
    [ObservableProperty] private string _visualEditorStatusText = "选择一个常见故障卡片，填写参数后生成 Scenario DSL；生成结果仍需通过 S0 Manifest / SHA-256 治理。";

    [ObservableProperty] private string _visualPlcId = "PLC1";
    [ObservableProperty] private string _visualPlcDisconnectAtMsText = "1000";
    [ObservableProperty] private string _visualPlcOutageDurationMsText = "9000";

    [ObservableProperty] private string _visualTrafficVehicleA = "RGV1";
    [ObservableProperty] private string _visualTrafficVehicleB = "RGV2";
    [ObservableProperty] private string _visualTrafficSegmentA = "S1";
    [ObservableProperty] private string _visualTrafficSegmentB = "S2";
    [ObservableProperty] private string _visualTrafficLeaseMsText = "10000";
    [ObservableProperty] private string _visualTrafficDetectAtMsText = "30";

    [ObservableProperty] private string _visualExternalEndpointId = "MES1";
    [ObservableProperty] private string _visualExternalOperation = "Order.Push";
    [ObservableProperty] private string _visualExternalFaultDurationMsText = "50";
    [ObservableProperty] private string _visualExternalTimeoutMsText = "20";
    [ObservableProperty] private string _visualExternalRetryDelayMsText = "60";

    [ObservableProperty] private string _visualHealthAssetId = "RGV-S6";
    [ObservableProperty] private string _visualHealthDurationHoursText = "72";
    [ObservableProperty] private string _visualHealthTargetScoreText = "30";
    [ObservableProperty] private string _visualHealthTargetRiskText = "0.95";
    [ObservableProperty] private string _visualHealthRulMedianHoursText = "100";

    [ObservableProperty] private string _visualRecoveryMissionId = "M1";
    [ObservableProperty] private string _visualRecoveryPlcBlockKey = "PLC1.DB100";
    [ObservableProperty] private string _visualRecoveryVehicleId = "RGV1";
    [ObservableProperty] private string _visualRecoveryLoadId = "LOAD1";
    [ObservableProperty] private string _visualRecoverySourceNodeId = "N1";
    [ObservableProperty] private string _visualRecoveryMiddleNodeId = "N2";
    [ObservableProperty] private string _visualRecoveryDestinationNodeId = "N3";
    [ObservableProperty] private string _visualRecoveryExternalEndpointId = "MES1";
    [ObservableProperty] private string _visualRecoveryHealthAssetId = "ASSET1";

    [RelayCommand]
    private void GenerateVisualPlcScenario() => TryLoadVisualPlcScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterVisualPlcScenarioAsync()
    {
        if (TryLoadVisualPlcScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateVisualTrafficScenario() => TryLoadVisualTrafficScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterVisualTrafficScenarioAsync()
    {
        if (TryLoadVisualTrafficScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateVisualExternalScenario() => TryLoadVisualExternalScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterVisualExternalScenarioAsync()
    {
        if (TryLoadVisualExternalScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateVisualHealthScenario() => TryLoadVisualHealthScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterVisualHealthScenarioAsync()
    {
        if (TryLoadVisualHealthScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateVisualRecoveryScenario() => TryLoadVisualRecoveryScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterVisualRecoveryScenarioAsync()
    {
        if (TryLoadVisualRecoveryScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    private bool TryLoadVisualPlcScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;

        if (!TryRequired(VisualPlcId, "PLC Id", out var plcId) ||
            !TryLong(VisualPlcDisconnectAtMsText, "断线时间", 0, long.MaxValue, out var disconnectAt) ||
            !TryLong(VisualPlcOutageDurationMsText, "断线持续时间", 2, long.MaxValue, out var outageDuration))
            return false;

        long reconnectAt;
        long offlineAssertAt;
        long onlineAssertAt;
        long duration;
        try
        {
            reconnectAt = checked(disconnectAt + outageDuration);
            offlineAssertAt = checked(disconnectAt + Math.Max(1, outageDuration / 2));
            onlineAssertAt = checked(reconnectAt + 500);
            duration = checked(onlineAssertAt + 500);
        }
        catch (OverflowException)
        {
            return VisualError("PLC 时间参数溢出 Int64。请缩短断线时间或持续时间。");
        }

        var actions = new List<object>
        {
            Action("disconnect", disconnectAt, 0, "plc.connection.set", plcId,
                Payload(("Connected", false))),
            Action("reconnect", reconnectAt, 0, "plc.connection.set", plcId,
                Payload(("Connected", true)))
        };
        var assertions = new List<object>
        {
            Assertion("assert-offline", offlineAssertAt, 0, "plc.connected", plcId, false),
            Assertion("assert-online", onlineAssertAt, 0, "plc.connected", plcId, true)
        };

        var scenarioId = $"visual-plc-{Slug(plcId)}-disconnect";
        ApplyVisualScenario(
            scenarioId,
            seed,
            start,
            duration,
            actions,
            assertions,
            $"PLC {plcId} 在 {disconnectAt}ms 断线，持续 {outageDuration}ms 后恢复；包含离线/恢复断言。");
        return true;
    }

    private bool TryLoadVisualTrafficScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;

        if (!TryRequired(VisualTrafficVehicleA, "车辆 A", out var vehicleA) ||
            !TryRequired(VisualTrafficVehicleB, "车辆 B", out var vehicleB) ||
            !TryRequired(VisualTrafficSegmentA, "区段 A", out var segmentA) ||
            !TryRequired(VisualTrafficSegmentB, "区段 B", out var segmentB) ||
            !TryLong(VisualTrafficLeaseMsText, "路权租约", 100, long.MaxValue, out var leaseMs) ||
            !TryLong(VisualTrafficDetectAtMsText, "死锁检测时间", 30, long.MaxValue - 20, out var detectAt))
            return false;

        if (string.Equals(vehicleA, vehicleB, StringComparison.OrdinalIgnoreCase))
            return VisualError("双 RGV 死锁场景要求车辆 A 与车辆 B 不同。");
        if (string.Equals(segmentA, segmentB, StringComparison.OrdinalIgnoreCase))
            return VisualError("双 RGV 死锁场景要求区段 A 与区段 B 不同。");

        var ownAt = detectAt - 20;
        var waitAt = detectAt - 10;
        var assertAt = detectAt + 10;
        var duration = assertAt + 10;

        var actions = new List<object>
        {
            Action("segment-a", 0, 0, "rgv.segment.define", segmentA,
                Payload(("FromNodeId", "N1"), ("ToNodeId", "N2"), ("LengthMillimeters", 1000), ("SpeedLimitMillimetersPerSecond", 1000), ("Enabled", true))),
            Action("segment-b", 0, 1, "rgv.segment.define", segmentB,
                Payload(("FromNodeId", "N2"), ("ToNodeId", "N1"), ("LengthMillimeters", 1000), ("SpeedLimitMillimetersPerSecond", 1000), ("Enabled", true))),
            Action("vehicle-a", 0, 2, "rgv.vehicle.define", vehicleA,
                Payload(("InitialNodeId", "N1"), ("SpeedMillimetersPerSecond", 1000), ("BatteryPercent", 100), ("IsOnline", true), ("Capabilities", "Carry"))),
            Action("vehicle-b", 0, 3, "rgv.vehicle.define", vehicleB,
                Payload(("InitialNodeId", "N2"), ("SpeedMillimetersPerSecond", 1000), ("BatteryPercent", 100), ("IsOnline", true), ("Capabilities", "Carry"))),
            Action("zone-a", 0, 4, "traffic.zone.define", "Z1",
                Payload(("SegmentIds", new[] { segmentA }), ("Capacity", 1), ("Kind", "SharedSegment"))),
            Action("zone-b", 0, 5, "traffic.zone.define", "Z2",
                Payload(("SegmentIds", new[] { segmentB }), ("Capacity", 1), ("Kind", "SharedSegment"))),
            Action("vehicle-a-own", ownAt, 0, "traffic.reserve", vehicleA,
                Payload(("SegmentId", segmentA), ("Priority", 10), ("LeaseMilliseconds", leaseMs))),
            Action("vehicle-b-own", ownAt, 1, "traffic.reserve", vehicleB,
                Payload(("SegmentId", segmentB), ("Priority", 20), ("LeaseMilliseconds", leaseMs))),
            Action("vehicle-a-wait", waitAt, 0, "traffic.reserve", vehicleA,
                Payload(("SegmentId", segmentB), ("Priority", 10), ("LeaseMilliseconds", leaseMs))),
            Action("vehicle-b-wait", waitAt, 1, "traffic.reserve", vehicleB,
                Payload(("SegmentId", segmentA), ("Priority", 20), ("LeaseMilliseconds", leaseMs))),
            Action("detect", detectAt, 0, "traffic.deadlock.detect", "global", Payload())
        };
        var assertions = new List<object>
        {
            Assertion("deadlock-exists", assertAt, 0, "traffic.deadlock.exists", "global", true),
            Assertion("vehicle-a-waits", assertAt, 1, "traffic.waits-for", vehicleA, vehicleB),
            Assertion("vehicle-b-waits", assertAt, 2, "traffic.waits-for", vehicleB, vehicleA)
        };

        var scenarioId = $"visual-traffic-{Slug(vehicleA)}-{Slug(vehicleB)}-deadlock";
        ApplyVisualScenario(
            scenarioId,
            seed,
            start,
            duration,
            actions,
            assertions,
            $"{vehicleA}/{vehicleB} 分别持有 {segmentA}/{segmentB} 后交叉申请，{detectAt}ms 执行 deadlock detection。 ");
        return true;
    }

    private bool TryLoadVisualExternalScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;

        if (!TryRequired(VisualExternalEndpointId, "外部 Endpoint", out var endpointId) ||
            !TryRequired(VisualExternalOperation, "Operation", out var operation) ||
            !TryLong(VisualExternalFaultDurationMsText, "超时故障窗口", 1, long.MaxValue - 1000, out var faultDuration) ||
            !TryLong(VisualExternalTimeoutMsText, "请求 Timeout", 1, int.MaxValue, out var timeoutMs) ||
            !TryLong(VisualExternalRetryDelayMsText, "Retry Delay", 1, long.MaxValue - 1000, out var retryDelayMs))
            return false;

        var assertAt = checked(Math.Max(faultDuration + 10, retryDelayMs + 10));
        var duration = checked(assertAt + 10);

        var actions = new List<object>
        {
            Action("endpoint", 0, 0, "external.endpoint.define", endpointId,
                Payload(("Kind", "Mes"))),
            Action("fault", 0, 1, "external.fault.apply", "F1",
                Payload(("EndpointId", endpointId), ("Kind", "Timeout"), ("StartsAtOffsetMilliseconds", 0), ("EndsAtOffsetMilliseconds", faultDuration), ("DelayMilliseconds", 0))),
            Action("invoke", 0, 2, "external.request.invoke", endpointId,
                Payload(("Operation", operation), ("IdempotencyKey", "visual-scenario-key"), ("PayloadHash", new string('a', 64)), ("MaxAttempts", 2), ("TimeoutMilliseconds", timeoutMs), ("RetryDelayMilliseconds", retryDelayMs)))
        };
        var assertions = new List<object>
        {
            Assertion("request-state", assertAt, 0, "external.request.state", "EXTREQ-000000000001", "Succeeded"),
            Assertion("attempts", assertAt, 1, "external.request.attempts", "EXTREQ-000000000001", 2),
            Assertion("circuit", assertAt, 2, "external.circuit.state", endpointId, "Closed"),
            Assertion("fault-ended", assertAt, 3, "external.fault.active", "F1", false)
        };

        var scenarioId = $"visual-external-{Slug(endpointId)}-timeout";
        ApplyVisualScenario(
            scenarioId,
            seed,
            start,
            duration,
            actions,
            assertions,
            $"{endpointId}/{operation} 注入 Timeout，故障窗口 {faultDuration}ms，Retry Delay {retryDelayMs}ms，验证重试恢复与 Circuit Closed。 ");
        return true;
    }

    private bool TryLoadVisualHealthScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;

        if (!TryRequired(VisualHealthAssetId, "Health Asset", out var assetId) ||
            !TryLong(VisualHealthDurationHoursText, "退化时长(h)", 2, 720, out var durationHours) ||
            !TryDouble(VisualHealthTargetScoreText, "目标 Health Score", 0, 95, out var targetScore) ||
            !TryDouble(VisualHealthTargetRiskText, "目标 Fusion Risk", 0.05, 1, out var targetRisk) ||
            !TryDouble(VisualHealthRulMedianHoursText, "目标 RUL Median(h)", 1, 100000, out var rulMedianHours))
            return false;

        var durationMs = checked(durationHours * 3_600_000L);
        var firstAt = Math.Max(3_600_000L, durationMs * 2 / 3);
        var finalAt = durationMs;
        var firstScore = Math.Min(95d, Math.Max(targetScore + 10d, (100d + targetScore) / 2d));
        var firstRisk = Math.Min(targetRisk, (0.05d + targetRisk) / 2d);
        var firstRulMedian = Math.Max(rulMedianHours + 1d, rulMedianHours * 1.8d);

        var actions = new List<object>
        {
            Action("define", 0, 0, "health.asset.define", assetId,
                Payload(("InitialHealthScore", 100), ("InitialFusionRiskScore", 0.05), ("IndependentSourceCount", 1))),
            Action("degrade-first", firstAt, 0, "health.profile.linear", assetId,
                Payload(("TargetHealthScore", firstScore), ("TargetFusionRiskScore", firstRisk), ("SampleIntervalMilliseconds", 3_600_000), ("Reason", "visual-bearing-wear"))),
            Action("forecast-first", firstAt, 1, "health.forecast.oracle", assetId,
                Payload(("FailureProbability24Hours", 0.10), ("FailureProbability72Hours", 0.25), ("FailureProbability168Hours", 0.45),
                    ("RulLowerHours", firstRulMedian * 0.65d), ("RulMedianHours", firstRulMedian), ("RulUpperHours", firstRulMedian * 1.45d), ("Phase", "degradation"))),
            Action("degrade-final", finalAt, 0, "health.profile.linear", assetId,
                Payload(("TargetHealthScore", targetScore), ("TargetFusionRiskScore", targetRisk), ("SampleIntervalMilliseconds", 3_600_000), ("Reason", "visual-bearing-wear"))),
            Action("forecast-final", finalAt, 1, "health.forecast.oracle", assetId,
                Payload(("FailureProbability24Hours", 0.25), ("FailureProbability72Hours", 0.50), ("FailureProbability168Hours", 0.80),
                    ("RulLowerHours", rulMedianHours * 0.4d), ("RulMedianHours", rulMedianHours), ("RulUpperHours", rulMedianHours * 1.6d), ("Phase", "degradation"))),
            Action("outcome", finalAt, 2, "health.outcome.record", assetId,
                Payload(("Kind", "ObservedFailure"), ("Note", "visual-synthetic-bearing-failure")))
        };
        var assertions = new List<object>
        {
            Assertion("score", finalAt, 0, "health.asset.score.at-most", assetId, targetScore),
            Assertion("feature", finalAt, 1, "health.feature.valid", assetId, true),
            Assertion("contract", finalAt, 2, "health.forecast.contract.valid", assetId, true),
            Assertion("rul", finalAt, 3, "health.rul.nonincreasing", assetId, true),
            Assertion("probability", finalAt, 4, "health.probability.nondecreasing", assetId, true),
            Assertion("outcome-kind", finalAt, 5, "health.outcome.kind", assetId, "ObservedFailure")
        };

        var scenarioId = $"visual-health-{Slug(assetId)}-{durationHours}h";
        ApplyVisualScenario(
            scenarioId,
            seed,
            start,
            durationMs,
            actions,
            assertions,
            $"{assetId} 在虚拟 {durationHours}h 内线性退化到 Health={targetScore:0.##}, Risk={targetRisk:0.###}, RUL Median={rulMedianHours:0.##}h。 ");
        return true;
    }

    private bool TryLoadVisualRecoveryScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;

        if (!TryRequired(VisualRecoveryMissionId, "MissionId", out var missionId) ||
            !TryRequired(VisualRecoveryPlcBlockKey, "PLC Block", out var plcBlockKey) ||
            !TryRequired(VisualRecoveryVehicleId, "VehicleId", out var vehicleId) ||
            !TryRequired(VisualRecoveryLoadId, "LoadId", out var loadId) ||
            !TryRequired(VisualRecoverySourceNodeId, "Source Node", out var sourceNodeId) ||
            !TryRequired(VisualRecoveryMiddleNodeId, "Middle Node", out var middleNodeId) ||
            !TryRequired(VisualRecoveryDestinationNodeId, "Destination Node", out var destinationNodeId) ||
            !TryRequired(VisualRecoveryExternalEndpointId, "External Endpoint", out var endpointId) ||
            !TryRequired(VisualRecoveryHealthAssetId, "Health Asset", out var healthAssetId))
            return false;

        if (string.Equals(sourceNodeId, middleNodeId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(middleNodeId, destinationNodeId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sourceNodeId, destinationNodeId, StringComparison.OrdinalIgnoreCase))
            return VisualError("全链恢复场景要求 Source / Middle / Destination 三个节点互不相同。");

        const long duration = 2300;
        var actions = new List<object>
        {
            Action("define", 0, 0, "integration.mission.define", missionId,
                Payload(
                    ("PlcBlockKey", plcBlockKey),
                    ("VehicleId", vehicleId),
                    ("LoadId", loadId),
                    ("SourceNodeId", sourceNodeId),
                    ("DestinationNodeId", destinationNodeId),
                    ("ExternalEndpointId", endpointId),
                    ("ExternalSystemKind", "Mes"),
                    ("HealthAssetId", healthAssetId),
                    ("Priority", 100),
                    ("VehicleSpeedMillimetersPerSecond", 1000),
                    ("VehicleBatteryPercent", 100),
                    ("InitialHealthScore", 95),
                    ("InitialFusionRiskScore", 0.05),
                    ("Segments", new object[]
                    {
                        Payload(("SegmentId", "S1"), ("FromNodeId", sourceNodeId), ("ToNodeId", middleNodeId), ("LengthMillimeters", 1000), ("SpeedLimitMillimetersPerSecond", 1000)),
                        Payload(("SegmentId", "S2"), ("FromNodeId", middleNodeId), ("ToNodeId", destinationNodeId), ("LengthMillimeters", 1000), ("SpeedLimitMillimetersPerSecond", 1000))
                    }))),
            Action("dispatch", 10, 0, "integration.mission.dispatch", missionId, Payload()),
            Action("advance-1", 1010, 0, "integration.mission.advance", missionId, Payload()),
            Action("advance-2", 2010, 0, "integration.mission.advance", missionId, Payload()),
            Action("ack-1", 2100, 0, "integration.mission.ack", missionId, Payload()),
            Action("ack-replay", 2200, 0, "integration.mission.ack", missionId, Payload())
        };
        var assertions = new List<object>
        {
            Assertion("state", 2300, 0, "integration.mission.state", missionId, "Acknowledged"),
            Assertion("consistent", 2300, 1, "integration.mission.consistent", missionId, true),
            Assertion("exactly-once", 2300, 2, "integration.external.exactly-once", missionId, true)
        };

        var scenarioId = $"visual-recovery-{Slug(missionId)}";
        ApplyVisualScenario(
            scenarioId,
            seed,
            start,
            duration,
            actions,
            assertions,
            $"Mission {missionId}：{sourceNodeId} → {middleNodeId} → {destinationNodeId}，重复 ACK 验证状态一致性与 external exactly-once。 ");
        return true;
    }

    private bool TryGetVisualCommon(out long seed, out DateTimeOffset start)
    {
        seed = 0;
        start = default;
        if (!long.TryParse(VisualScenarioSeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed) || seed == 0)
            return VisualError("可视化场景 Seed 必须是非 0 Int64。");
        if (!DateTimeOffset.TryParse(VisualScenarioStartUtcText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out start))
            return VisualError("StartTimeUtc 格式无效。建议使用 2026-08-11T00:00:00+00:00。 ");
        return true;
    }

    private bool TryRequired(string? raw, string field, out string value)
    {
        value = raw?.Trim() ?? string.Empty;
        return value.Length > 0 || VisualError($"{field} 不能为空。");
    }

    private bool TryLong(string? raw, string field, long min, long max, out long value)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            return true;
        return VisualError($"{field} 必须在 {min}～{max} 范围内。");
    }

    private bool TryDouble(string? raw, string field, double min, double max, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) && value >= min && value <= max)
            return true;
        return VisualError($"{field} 必须在 {min.ToString(CultureInfo.InvariantCulture)}～{max.ToString(CultureInfo.InvariantCulture)} 范围内。");
    }

    private bool VisualError(string message)
    {
        VisualEditorStatusText = message;
        StatusText = message;
        return false;
    }

    private void ApplyVisualScenario(
        string scenarioId,
        long seed,
        DateTimeOffset start,
        long durationMilliseconds,
        IReadOnlyList<object> actions,
        IReadOnlyList<object> assertions,
        string summary)
    {
        var document = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["ScenarioId"] = scenarioId,
            ["Version"] = "1.0.0",
            ["Seed"] = seed,
            ["StartTimeUtc"] = start,
            ["DurationMilliseconds"] = durationMilliseconds,
            ["StopOnAssertionFailure"] = true,
            ["Actions"] = actions,
            ["Assertions"] = assertions
        };

        ScenarioId = scenarioId;
        ScenarioVersion = "1.0.0";
        ScenarioSeedText = seed.ToString(CultureInfo.InvariantCulture);
        ScenarioFile = $"{scenarioId}.json";
        ScenarioSource = "Wcs.Desktop Visual Scenario Editor";
        ScenarioApprovedBy = "simulation-operator";
        ScenarioJson = JsonSerializer.Serialize(document, VisualScenarioJsonOptions);
        SpeedFactorText = "1";
        Assertions.Clear();
        CheckpointHash = "-";
        CheckpointStateText = summary;
        VisualEditorStatusText = $"已生成 {scenarioId}：Actions={actions.Count}, Assertions={assertions.Count}。可直接“生成并注册”，或到场景治理页检查 JSON。";
        StatusText = VisualEditorStatusText;
    }

    private static Dictionary<string, object?> Payload(params (string Name, object? Value)[] values)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
            payload[name] = value;
        return payload;
    }

    private static object Action(string id, long atMilliseconds, int order, string kind, string target, object payload) =>
        new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["AtMilliseconds"] = atMilliseconds,
            ["Order"] = order,
            ["Kind"] = kind,
            ["Target"] = target,
            ["Payload"] = payload
        };

    private static object Assertion(string id, long atMilliseconds, int order, string kind, string target, object? expected) =>
        new Dictionary<string, object?>
        {
            ["Id"] = id,
            ["AtMilliseconds"] = atMilliseconds,
            ["Order"] = order,
            ["Kind"] = kind,
            ["Target"] = target,
            ["Expected"] = expected
        };

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        while (builder.Length > 0 && builder[^1] == '-')
            builder.Length--;
        return builder.Length == 0 ? "scenario" : builder.ToString();
    }
}
