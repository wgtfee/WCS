namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;

public partial class SimulationVerificationViewModel
{
    private static readonly string[] SupportedTrafficZoneKinds =
        ["SharedSegment", "OpposingDirection", "Merge", "Intersection", "Custom"];
    private static readonly string[] SupportedExternalSystemKinds =
        ["Mes", "SqlServer", "Http", "Network", "Custom"];
    private static readonly string[] SupportedExternalFaultKinds =
        ["Timeout", "Unavailable", "HttpStatus", "InvalidResponse", "DuplicateResponse", "SqlDeadlock", "SqlCommandTimeout", "ConnectionReset", "HighLatency", "PacketLoss", "HalfOpen"];

    [ObservableProperty] private string _trafficExternalStatusText =
        "S4/S5 操作只生成受治理 Scenario DSL；不会调用生产 Traffic、MES、SQL、HTTP、Network 或真实 HIL。";

    [ObservableProperty] private string _trafficVehicleId = "RGV1";
    [ObservableProperty] private string _trafficVehicleBId = "RGV2";
    [ObservableProperty] private string _trafficSourceNodeId = "N1";
    [ObservableProperty] private string _trafficMiddleNodeId = "N2";
    [ObservableProperty] private string _trafficDestinationNodeId = "N3";
    [ObservableProperty] private string _trafficSegmentIdsText = "S1,S2";
    [ObservableProperty] private string _trafficZoneId = "Z1";
    [ObservableProperty] private string _trafficZoneBId = "Z2";
    [ObservableProperty] private string _trafficZoneKind = "SharedSegment";
    [ObservableProperty] private string _trafficZoneBKind = "OpposingDirection";
    [ObservableProperty] private string _trafficCapacityText = "1";
    [ObservableProperty] private string _trafficSegmentLengthMmText = "1000";
    [ObservableProperty] private string _trafficVehicleSpeedMmPerSecondText = "1000";
    [ObservableProperty] private string _trafficPriorityText = "10";
    [ObservableProperty] private string _trafficPriorityBText = "20";
    [ObservableProperty] private string _trafficLeaseMsText = "10000";
    [ObservableProperty] private string _trafficLookAheadText = "2";
    [ObservableProperty] private string _trafficDeadlockDetectAtMsText = "30";

    [ObservableProperty] private string _externalEndpointId = "MES1";
    [ObservableProperty] private string _externalSystemKind = "Mes";
    [ObservableProperty] private string _externalFaultId = "F-EXT-1";
    [ObservableProperty] private string _externalFaultKind = "Timeout";
    [ObservableProperty] private string _externalFaultStartMsText = "10";
    [ObservableProperty] private string _externalFaultEndMsText = "80";
    [ObservableProperty] private string _externalHttpStatusCodeText = "503";
    [ObservableProperty] private string _externalDelayMsText = "0";
    [ObservableProperty] private string _externalErrorCode = "";
    [ObservableProperty] private string _externalOperation = "Order.Push";
    [ObservableProperty] private string _externalIdempotencyKey = "visual-external-key";
    [ObservableProperty] private string _externalPayloadHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    [ObservableProperty] private string _externalMaxAttemptsText = "2";
    [ObservableProperty] private string _externalTimeoutMsText = "20";
    [ObservableProperty] private string _externalRetryDelayMsText = "100";

    public string TrafficZoneKindsText => string.Join(" / ", SupportedTrafficZoneKinds);
    public string ExternalSystemKindsText => string.Join(" / ", SupportedExternalSystemKinds);
    public string ExternalFaultKindsText => string.Join(" / ", SupportedExternalFaultKinds);

    [RelayCommand]
    private void SelectTrafficZoneKind(string? kind)
    {
        if (!TrySupportedText(kind, SupportedTrafficZoneKinds, out var normalized))
        {
            PanelError($"Zone Kind 只支持：{TrafficZoneKindsText}。");
            return;
        }
        TrafficZoneKind = normalized;
        TrafficExternalStatusText = $"已选择 Zone Kind：{normalized}。";
    }

    [RelayCommand]
    private void SelectExternalSystemKind(string? kind)
    {
        if (!TrySupportedText(kind, SupportedExternalSystemKinds, out var normalized))
        {
            PanelError($"External System Kind 只支持：{ExternalSystemKindsText}。");
            return;
        }
        ExternalSystemKind = normalized;
        TrafficExternalStatusText = $"已选择 External System：{normalized}。";
    }

    [RelayCommand]
    private void SelectExternalFaultKind(string? kind)
    {
        if (!TrySupportedText(kind, SupportedExternalFaultKinds, out var normalized))
        {
            PanelError($"External Fault Kind 只支持：{ExternalFaultKindsText}。");
            return;
        }
        ExternalFaultKind = normalized;
        TrafficExternalStatusText = $"已选择 External Fault：{normalized}。";
    }

    [RelayCommand]
    private void GenerateTrafficOperationsScenario() => TryLoadTrafficOperationsScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterTrafficOperationsScenarioAsync()
    {
        if (TryLoadTrafficOperationsScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateTrafficDeadlockScenario() => TryLoadTrafficDeadlockScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterTrafficDeadlockScenarioAsync()
    {
        if (TryLoadTrafficDeadlockScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateExternalOperationsScenario() => TryLoadExternalOperationsScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterExternalOperationsScenarioAsync()
    {
        if (TryLoadExternalOperationsScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    private bool TryLoadTrafficOperationsScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start) || !TryReadTrafficInput(out var input))
            return false;

        var travelMs = checked((input.SegmentLengthMm * 1000L + input.SpeedMmPerSecond - 1) / input.SpeedMmPerSecond);
        var moveAt = Math.Max(100L, travelMs);
        var reserveForExpiryAt = checked(moveAt + 10);
        var expireAt = checked(reserveForExpiryAt + 20);
        var assertAt = checked(expireAt + 10);
        var duration = checked(assertAt + 10);

        var actions = new List<object>
        {
            Action("segment-a", 0, 0, "rgv.segment.define", input.SegmentA,
                Payload(("FromNodeId", input.SourceNode), ("ToNodeId", input.MiddleNode),
                    ("LengthMillimeters", input.SegmentLengthMm), ("SpeedLimitMillimetersPerSecond", input.SpeedMmPerSecond), ("Enabled", true))),
            Action("segment-b", 0, 1, "rgv.segment.define", input.SegmentB,
                Payload(("FromNodeId", input.MiddleNode), ("ToNodeId", input.DestinationNode),
                    ("LengthMillimeters", input.SegmentLengthMm), ("SpeedLimitMillimetersPerSecond", input.SpeedMmPerSecond), ("Enabled", true))),
            Action("vehicle", 0, 2, "rgv.vehicle.define", input.VehicleA,
                Payload(("InitialNodeId", input.SourceNode), ("SpeedMillimetersPerSecond", input.SpeedMmPerSecond),
                    ("BatteryPercent", 100), ("IsOnline", true), ("Capabilities", "Carry"))),
            Action("route", 0, 3, "rgv.route.assign", input.VehicleA,
                Payload(("SegmentIds", new[] { input.SegmentA, input.SegmentB }))),
            Action("zone-a", 0, 4, "traffic.zone.define", input.ZoneA,
                Payload(("SegmentIds", new[] { input.SegmentA }), ("Capacity", input.ZoneCapacity), ("Kind", input.ZoneKindA))),
            Action("zone-b", 0, 5, "traffic.zone.define", input.ZoneB,
                Payload(("SegmentIds", new[] { input.SegmentB }), ("Capacity", input.ZoneCapacity), ("Kind", input.ZoneKindB))),
            Action("reserve-explicit", 10, 0, "traffic.reserve", input.VehicleA,
                Payload(("SegmentId", input.SegmentA), ("Priority", input.PriorityA), ("LeaseMilliseconds", input.LeaseMs))),
            Action("release-explicit", 20, 0, "traffic.release", input.VehicleA,
                Payload(("SegmentId", input.SegmentA))),
            Action("rolling-reserve", 30, 0, "traffic.rolling.reserve", input.VehicleA,
                Payload(("LookAheadSegments", input.RollingLookAhead), ("Priority", input.PriorityA), ("LeaseMilliseconds", input.LeaseMs))),
            Action("advance", moveAt, 0, "rgv.vehicle.advance", input.VehicleA, Payload()),
            Action("rolling-release", moveAt, 1, "traffic.rolling.release", input.VehicleA, Payload()),
            Action("release-remaining", moveAt + 1, 0, "traffic.release", input.VehicleA,
                Payload(("SegmentId", input.SegmentB))),
            Action("reserve-for-expiry", reserveForExpiryAt, 0, "traffic.reserve", input.VehicleA,
                Payload(("SegmentId", input.SegmentB), ("Priority", input.PriorityA), ("LeaseMilliseconds", 10))),
            Action("expire", expireAt, 0, "traffic.expire", "all", Payload())
        };
        var assertions = new List<object>
        {
            Assertion("zone-a-available", assertAt, 0, "traffic.zone.available", input.ZoneA, true),
            Assertion("zone-b-available", assertAt, 1, "traffic.zone.available", input.ZoneB, true)
        };

        ApplyVisualScenario($"visual-traffic-{Slug(input.VehicleA)}-lifecycle", seed, start, duration, actions, assertions,
            $"S4 lifecycle：zone define → reserve/release → rolling reserve/release → expire；Vehicle={input.VehicleA}。 ");
        TrafficExternalStatusText = "已生成可执行 S4 路权生命周期 DSL。";
        return true;
    }

    private bool TryLoadTrafficDeadlockScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start) || !TryReadTrafficInput(out var input) ||
            !TryLong(TrafficDeadlockDetectAtMsText, "Deadlock Detect At", 20, long.MaxValue - 30, out var detectAt))
            return false;
        if (string.Equals(input.VehicleA, input.VehicleB, StringComparison.OrdinalIgnoreCase))
            return PanelError("Deadlock 场景要求 Vehicle A 与 Vehicle B 不同。");

        var ownAt = detectAt - 20;
        var waitAt = detectAt - 10;
        var resolveAt = detectAt + 10;
        var assertAt = resolveAt + 10;
        var duration = assertAt + 10;
        const string deadlockId = "DL-000000000007";

        var actions = new List<object>
        {
            Action("segment-a", 0, 0, "rgv.segment.define", input.SegmentA,
                Payload(("FromNodeId", input.SourceNode), ("ToNodeId", input.MiddleNode),
                    ("LengthMillimeters", input.SegmentLengthMm), ("SpeedLimitMillimetersPerSecond", input.SpeedMmPerSecond), ("Enabled", true))),
            Action("segment-b", 0, 1, "rgv.segment.define", input.SegmentB,
                Payload(("FromNodeId", input.MiddleNode), ("ToNodeId", input.SourceNode),
                    ("LengthMillimeters", input.SegmentLengthMm), ("SpeedLimitMillimetersPerSecond", input.SpeedMmPerSecond), ("Enabled", true))),
            Action("vehicle-a", 0, 2, "rgv.vehicle.define", input.VehicleA,
                Payload(("InitialNodeId", input.SourceNode), ("SpeedMillimetersPerSecond", input.SpeedMmPerSecond), ("BatteryPercent", 100), ("IsOnline", true), ("Capabilities", "Carry"))),
            Action("vehicle-b", 0, 3, "rgv.vehicle.define", input.VehicleB,
                Payload(("InitialNodeId", input.MiddleNode), ("SpeedMillimetersPerSecond", input.SpeedMmPerSecond), ("BatteryPercent", 100), ("IsOnline", true), ("Capabilities", "Carry"))),
            Action("zone-a", 0, 4, "traffic.zone.define", input.ZoneA,
                Payload(("SegmentIds", new[] { input.SegmentA }), ("Capacity", 1), ("Kind", input.ZoneKindA))),
            Action("zone-b", 0, 5, "traffic.zone.define", input.ZoneB,
                Payload(("SegmentIds", new[] { input.SegmentB }), ("Capacity", 1), ("Kind", input.ZoneKindB))),
            Action("hold-a", ownAt, 0, "traffic.reserve", input.VehicleA,
                Payload(("SegmentId", input.SegmentA), ("Priority", input.PriorityA), ("LeaseMilliseconds", input.LeaseMs))),
            Action("hold-b", ownAt, 1, "traffic.reserve", input.VehicleB,
                Payload(("SegmentId", input.SegmentB), ("Priority", input.PriorityB), ("LeaseMilliseconds", input.LeaseMs))),
            Action("wait-a", waitAt, 0, "traffic.reserve", input.VehicleA,
                Payload(("SegmentId", input.SegmentB), ("Priority", input.PriorityA), ("LeaseMilliseconds", input.LeaseMs))),
            Action("wait-b", waitAt, 1, "traffic.reserve", input.VehicleB,
                Payload(("SegmentId", input.SegmentA), ("Priority", input.PriorityB), ("LeaseMilliseconds", input.LeaseMs))),
            Action("detect", detectAt, 0, "traffic.deadlock.detect", "all", Payload()),
            Action("resolve", resolveAt, 0, "traffic.deadlock.resolve", deadlockId, Payload())
        };
        var assertions = new List<object>
        {
            Assertion("wait-a", waitAt, 2, "traffic.waits-for", input.VehicleA, input.VehicleB),
            Assertion("wait-b", waitAt, 3, "traffic.waits-for", input.VehicleB, input.VehicleA),
            Assertion("deadlock-detected", detectAt, 1, "traffic.deadlock.exists", "all", true),
            Assertion("deadlock-cleared", resolveAt, 1, "traffic.deadlock.exists", "all", false)
        };

        ApplyVisualScenario($"visual-traffic-{Slug(input.VehicleA)}-{Slug(input.VehicleB)}-resolve", seed, start, duration, actions, assertions,
            $"S4 deadlock：{input.VehicleA}/{input.VehicleB} 交叉等待，detect → resolve；DeadlockId={deadlockId}。 ");
        TrafficExternalStatusText = "已生成可执行 S4 Deadlock Detect + Resolve DSL。";
        return true;
    }

    private bool TryLoadExternalOperationsScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start) ||
            !TryRequired(ExternalEndpointId, "Endpoint", out var endpointId) ||
            !TryRequired(ExternalFaultId, "Fault Id", out var faultId) ||
            !TryRequired(ExternalOperation, "Operation", out var operation) ||
            !TryRequired(ExternalIdempotencyKey, "Idempotency Key", out var idempotencyKey) ||
            !TryRequired(ExternalPayloadHash, "Payload Hash", out var payloadHash) ||
            !TrySupportedText(ExternalSystemKind, SupportedExternalSystemKinds, out var systemKind) ||
            !TrySupportedText(ExternalFaultKind, SupportedExternalFaultKinds, out var faultKind) ||
            !TryLong(ExternalFaultStartMsText, "Fault Start", 0, long.MaxValue - 1000, out var faultStart) ||
            !TryLong(ExternalFaultEndMsText, "Fault End", faultStart + 1, long.MaxValue - 100, out var faultEnd) ||
            !TryLong(ExternalDelayMsText, "Delay", 0, 31_536_000_000L, out var delay) ||
            !TryInt(ExternalMaxAttemptsText, "Max Attempts", 1, 100, out var maxAttempts) ||
            !TryLong(ExternalTimeoutMsText, "Timeout", 1, 31_536_000_000L, out var timeout) ||
            !TryLong(ExternalRetryDelayMsText, "Retry Delay", 0, 31_536_000_000L, out var retryDelay))
            return false;
        if (!IsSha256Hex(payloadHash))
            return PanelError("Payload Hash 必须是 64 位十六进制 SHA-256。 ");

        int? httpStatus = null;
        if (string.Equals(faultKind, "HttpStatus", StringComparison.Ordinal))
        {
            if (!TryInt(ExternalHttpStatusCodeText, "HTTP Status", 100, 599, out var parsedStatus))
                return false;
            httpStatus = parsedStatus;
        }

        var faultPayload = Payload(
            ("EndpointId", endpointId), ("Kind", faultKind),
            ("StartsAtOffsetMilliseconds", faultStart), ("EndsAtOffsetMilliseconds", faultEnd),
            ("DelayMilliseconds", delay));
        if (httpStatus.HasValue)
            faultPayload["HttpStatusCode"] = httpStatus.Value;
        if (!string.IsNullOrWhiteSpace(ExternalErrorCode))
            faultPayload["ErrorCode"] = ExternalErrorCode.Trim();

        var clearAt = faultEnd + 1;
        var resetAt = faultEnd + 2;
        var assertAt = faultEnd + 3;
        var duration = faultEnd + 20;
        var actions = new List<object>
        {
            Action("endpoint", 0, 0, "external.endpoint.define", endpointId, Payload(("Kind", systemKind))),
            Action("fault", 0, 1, "external.fault.apply", faultId, faultPayload),
            Action("invoke", faultStart, 0, "external.request.invoke", endpointId,
                Payload(("Operation", operation), ("IdempotencyKey", idempotencyKey), ("PayloadHash", payloadHash),
                    ("MaxAttempts", maxAttempts), ("TimeoutMilliseconds", timeout), ("RetryDelayMilliseconds", retryDelay))),
            Action("fault-clear", clearAt, 0, "external.fault.clear", faultId, Payload()),
            Action("circuit-reset", resetAt, 0, "external.circuit.reset", endpointId, Payload())
        };
        var assertions = new List<object>
        {
            Assertion("fault-cleared", assertAt, 0, "external.fault.active", faultId, false),
            Assertion("circuit-closed", assertAt, 1, "external.circuit.state", endpointId, "Closed")
        };

        ApplyVisualScenario($"visual-external-{Slug(endpointId)}-{Slug(faultKind)}", seed, start, duration, actions, assertions,
            $"S5 External：{systemKind}/{endpointId} 注入 {faultKind}，request.invoke → fault.clear → circuit.reset。 ");
        TrafficExternalStatusText = $"已生成可执行 S5 {faultKind} DSL；不会访问真实 {systemKind}。";
        return true;
    }

    private bool TryReadTrafficInput(out TrafficInput input)
    {
        input = default;
        if (!TryRequired(TrafficVehicleId, "Vehicle A", out var vehicleA) ||
            !TryRequired(TrafficVehicleBId, "Vehicle B", out var vehicleB) ||
            !TryRequired(TrafficSourceNodeId, "Source Node", out var sourceNode) ||
            !TryRequired(TrafficMiddleNodeId, "Middle Node", out var middleNode) ||
            !TryRequired(TrafficDestinationNodeId, "Destination Node", out var destinationNode) ||
            !TryRequired(TrafficZoneId, "Zone A", out var zoneA) ||
            !TryRequired(TrafficZoneBId, "Zone B", out var zoneB) ||
            !TrySupportedText(TrafficZoneKind, SupportedTrafficZoneKinds, out var zoneKindA) ||
            !TrySupportedText(TrafficZoneBKind, SupportedTrafficZoneKinds, out var zoneKindB) ||
            !TryInt(TrafficCapacityText, "Capacity", 1, 16, out var capacity) ||
            !TryInt(TrafficSegmentLengthMmText, "Segment Length", 1, int.MaxValue, out var segmentLength) ||
            !TryInt(TrafficVehicleSpeedMmPerSecondText, "Vehicle Speed", 1, int.MaxValue, out var speed) ||
            !TryInt(TrafficPriorityText, "Priority A", int.MinValue, int.MaxValue, out var priorityA) ||
            !TryInt(TrafficPriorityBText, "Priority B", int.MinValue, int.MaxValue, out var priorityB) ||
            !TryLong(TrafficLeaseMsText, "Lease", 1, 31_536_000_000L, out var lease) ||
            !TryInt(TrafficLookAheadText, "LookAhead", 1, 10_000, out var lookAhead))
            return false;

        var segments = (TrafficSegmentIdsText ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || string.Equals(segments[0], segments[1], StringComparison.OrdinalIgnoreCase))
            return PanelError("SegmentIds 当前要求正好两个不同区段，例如 S1,S2。 ");
        if (string.Equals(zoneA, zoneB, StringComparison.OrdinalIgnoreCase))
            return PanelError("Zone A 与 Zone B 必须不同。 ");

        input = new TrafficInput(vehicleA, vehicleB, sourceNode, middleNode, destinationNode,
            segments[0], segments[1], zoneA, zoneB, zoneKindA, zoneKindB,
            capacity, segmentLength, speed, priorityA, priorityB, lease, lookAhead);
        return true;
    }

    private bool TryInt(string? text, string name, int min, int max, out int value)
    {
        value = 0;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            return true;
        return PanelError($"{name} 必须是 {min}～{max} 的整数。 ");
    }

    private bool TrySupportedText(string? raw, IReadOnlyList<string> supported, out string value)
    {
        var candidate = raw?.Trim() ?? string.Empty;
        var match = supported.FirstOrDefault(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase));
        value = match ?? string.Empty;
        return match is not null;
    }

    private bool PanelError(string message)
    {
        TrafficExternalStatusText = message;
        StatusText = message;
        return false;
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private readonly record struct TrafficInput(
        string VehicleA, string VehicleB,
        string SourceNode, string MiddleNode, string DestinationNode,
        string SegmentA, string SegmentB,
        string ZoneA, string ZoneB, string ZoneKindA, string ZoneKindB,
        int ZoneCapacity, int SegmentLengthMm, int SpeedMmPerSecond,
        int PriorityA, int PriorityB, long LeaseMs, int RollingLookAhead);
}
