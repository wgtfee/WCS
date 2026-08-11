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

    [ObservableProperty] private string _trafficExternalStatusText = "S4/S5 操作只生成受治理 Scenario DSL；不会调用生产 Traffic、MES、SQL、HTTP 或真实网络。";
    [ObservableProperty] private string _trafficZoneId = "Z1";
    [ObservableProperty] private string _trafficZoneKind = "SharedSegment";
    [ObservableProperty] private string _trafficSegmentIdsText = "S1,S2";
    [ObservableProperty] private string _trafficCapacityText = "1";
    [ObservableProperty] private string _trafficVehicleId = "RGV1";
    [ObservableProperty] private string _trafficPriorityText = "100";
    [ObservableProperty] private string _trafficLeaseMsText = "10000";
    [ObservableProperty] private string _trafficLookAheadText = "2";
    [ObservableProperty] private string _trafficReserveAtMsText = "10";
    [ObservableProperty] private string _trafficReleaseAtMsText = "40";
    [ObservableProperty] private string _trafficExpireAtMsText = "60";
    [ObservableProperty] private string _trafficDeadlockId = "DL-1";
    [ObservableProperty] private string _trafficDeadlockDetectAtMsText = "70";
    [ObservableProperty] private string _trafficDeadlockResolveAtMsText = "80";

    [ObservableProperty] private string _externalEndpointId = "MES1";
    [ObservableProperty] private string _externalSystemKind = "Mes";
    [ObservableProperty] private string _externalFaultId = "F-EXT-1";
    [ObservableProperty] private string _externalFaultKind = "Timeout";
    [ObservableProperty] private string _externalFaultStartMsText = "10";
    [ObservableProperty] private string _externalFaultEndMsText = "80";
    [ObservableProperty] private string _externalHttpStatusCodeText = "503";
    [ObservableProperty] private string _externalDelayMsText = "50";
    [ObservableProperty] private string _externalErrorCode = "SIMULATED";
    [ObservableProperty] private string _externalOperation = "Order.Push";
    [ObservableProperty] private string _externalIdempotencyKey = "idem-001";
    [ObservableProperty] private string _externalPayloadHash = "payload-hash-001";
    [ObservableProperty] private string _externalMaxAttemptsText = "2";
    [ObservableProperty] private string _externalTimeoutMsText = "20";
    [ObservableProperty] private string _externalRetryDelayMsText = "10";
    [ObservableProperty] private string _externalInvokeAtMsText = "20";
    [ObservableProperty] private string _externalClearAtMsText = "90";
    [ObservableProperty] private string _externalCircuitResetAtMsText = "100";

    public string TrafficZoneKindsText => string.Join(" / ", SupportedTrafficZoneKinds);
    public string ExternalSystemKindsText => string.Join(" / ", SupportedExternalSystemKinds);
    public string ExternalFaultKindsText => string.Join(" / ", SupportedExternalFaultKinds);

    [RelayCommand]
    private void GenerateTrafficOperationsScenario() => TryLoadTrafficOperationsScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterTrafficOperationsScenarioAsync()
    {
        if (TryLoadTrafficOperationsScenario())
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
        if (!TryGetVisualCommon(out var seed, out var start) ||
            !TryRequired(TrafficZoneId, "Zone Id", out var zoneId) ||
            !TryRequired(TrafficVehicleId, "Vehicle Id", out var vehicleId) ||
            !TryRequired(TrafficZoneKind, "Zone Kind", out var zoneKind) ||
            !TryInt(TrafficCapacityText, "Capacity", 1, 16, out var capacity) ||
            !TryInt(TrafficPriorityText, "Priority", int.MinValue, int.MaxValue, out var priority) ||
            !TryLong(TrafficLeaseMsText, "Lease", 1, 86_400_000, out var lease) ||
            !TryInt(TrafficLookAheadText, "LookAhead", 1, 16, out var lookAhead) ||
            !TryLong(TrafficReserveAtMsText, "Reserve At", 0, long.MaxValue - 1000, out var reserveAt) ||
            !TryLong(TrafficReleaseAtMsText, "Release At", 0, long.MaxValue - 1000, out var releaseAt) ||
            !TryLong(TrafficExpireAtMsText, "Expire At", 0, long.MaxValue - 1000, out var expireAt) ||
            !TryLong(TrafficDeadlockDetectAtMsText, "Deadlock Detect At", 0, long.MaxValue - 1000, out var detectAt) ||
            !TryLong(TrafficDeadlockResolveAtMsText, "Deadlock Resolve At", 0, long.MaxValue - 1000, out var resolveAt))
            return false;

        if (!SupportedTrafficZoneKinds.Contains(zoneKind, StringComparer.OrdinalIgnoreCase))
            return VisualError($"Zone Kind 必须是：{TrafficZoneKindsText}");
        var segments = (TrafficSegmentIdsText ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length > 16)
            return VisualError("SegmentIds 必须提供 1~16 个逗号分隔区段。");

        var actions = new List<object>
        {
            Action("zone", 0, 0, "traffic.zone.define", zoneId, Payload(("SegmentIds", segments), ("Capacity", capacity), ("Kind", zoneKind))),
            Action("reserve", reserveAt, 0, "traffic.reserve", vehicleId, Payload(("SegmentId", segments[0]), ("Priority", priority), ("LeaseMilliseconds", lease))),
            Action("rolling-reserve", reserveAt + 1, 0, "traffic.rolling.reserve", vehicleId, Payload(("LookAheadSegments", lookAhead), ("Priority", priority), ("LeaseMilliseconds", lease))),
            Action("release", releaseAt, 0, "traffic.release", vehicleId, Payload(("SegmentId", segments[0]))),
            Action("rolling-release", releaseAt + 1, 0, "traffic.rolling.release", vehicleId, Payload()),
            Action("expire", expireAt, 0, "traffic.expire", "global", Payload()),
            Action("deadlock-detect", detectAt, 0, "traffic.deadlock.detect", "global", Payload())
        };
        if (!string.IsNullOrWhiteSpace(TrafficDeadlockId))
            actions.Add(Action("deadlock-resolve", resolveAt, 0, "traffic.deadlock.resolve", TrafficDeadlockId.Trim(), Payload()));

        var duration = new[] { releaseAt + 2, expireAt + 1, detectAt + 1, resolveAt + 1 }.Max() + 10;
        ApplyVisualScenario($"visual-traffic-{Slug(vehicleId)}-operations", seed, start, duration, actions, [],
            $"S4 Traffic：zone define、reserve/release、expire、rolling reserve/release、deadlock detect/resolve；Zone={zoneId} Vehicle={vehicleId}。");
        TrafficExternalStatusText = "已生成 S4 Traffic 受治理 DSL。Deadlock resolve 仅在填写实际 DeadlockId 时加入；运行前可在 JSON 中复核目标。";
        return true;
    }

    private bool TryLoadExternalOperationsScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start) ||
            !TryRequired(ExternalEndpointId, "Endpoint", out var endpointId) ||
            !TryRequired(ExternalSystemKind, "System Kind", out var systemKind) ||
            !TryRequired(ExternalFaultId, "Fault Id", out var faultId) ||
            !TryRequired(ExternalFaultKind, "Fault Kind", out var faultKind) ||
            !TryRequired(ExternalOperation, "Operation", out var operation) ||
            !TryRequired(ExternalIdempotencyKey, "Idempotency Key", out var idempotencyKey) ||
            !TryRequired(ExternalPayloadHash, "Payload Hash", out var payloadHash) ||
            !TryLong(ExternalFaultStartMsText, "Fault Start", 0, long.MaxValue - 1000, out var faultStart) ||
            !TryLong(ExternalFaultEndMsText, "Fault End", 1, long.MaxValue - 1000, out var faultEnd) ||
            !TryLong(ExternalDelayMsText, "Delay", 0, 86_400_000, out var delay) ||
            !TryInt(ExternalMaxAttemptsText, "Max Attempts", 1, 16, out var maxAttempts) ||
            !TryLong(ExternalTimeoutMsText, "Timeout", 1, 86_400_000, out var timeout) ||
            !TryLong(ExternalRetryDelayMsText, "Retry Delay", 0, 86_400_000, out var retryDelay) ||
            !TryLong(ExternalInvokeAtMsText, "Invoke At", 0, long.MaxValue - 1000, out var invokeAt) ||
            !TryLong(ExternalClearAtMsText, "Clear At", 0, long.MaxValue - 1000, out var clearAt) ||
            !TryLong(ExternalCircuitResetAtMsText, "Circuit Reset At", 0, long.MaxValue - 1000, out var resetAt))
            return false;

        if (!SupportedExternalSystemKinds.Contains(systemKind, StringComparer.OrdinalIgnoreCase))
            return VisualError($"System Kind 必须是：{ExternalSystemKindsText}");
        if (!SupportedExternalFaultKinds.Contains(faultKind, StringComparer.OrdinalIgnoreCase))
            return VisualError($"Fault Kind 必须是：{ExternalFaultKindsText}");
        if (faultEnd <= faultStart)
            return VisualError("Fault End 必须大于 Fault Start。");

        int? httpStatus = null;
        if (!string.IsNullOrWhiteSpace(ExternalHttpStatusCodeText))
        {
            if (!int.TryParse(ExternalHttpStatusCodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed is < 100 or > 599)
                return VisualError("HTTP Status 必须为空或 100~599。");
            httpStatus = parsed;
        }

        var actions = new List<object>
        {
            Action("endpoint", 0, 0, "external.endpoint.define", endpointId, Payload(("Kind", systemKind))),
            Action("fault", faultStart, 0, "external.fault.apply", faultId,
                Payload(("EndpointId", endpointId), ("Kind", faultKind), ("StartsAtOffsetMilliseconds", faultStart), ("EndsAtOffsetMilliseconds", faultEnd), ("HttpStatusCode", httpStatus), ("DelayMilliseconds", delay), ("ErrorCode", string.IsNullOrWhiteSpace(ExternalErrorCode) ? null : ExternalErrorCode.Trim()))),
            Action("invoke", invokeAt, 0, "external.request.invoke", endpointId,
                Payload(("Operation", operation), ("IdempotencyKey", idempotencyKey), ("PayloadHash", payloadHash), ("MaxAttempts", maxAttempts), ("TimeoutMilliseconds", timeout), ("RetryDelayMilliseconds", retryDelay))),
            Action("fault-clear", clearAt, 0, "external.fault.clear", faultId, Payload()),
            Action("circuit-reset", resetAt, 0, "external.circuit.reset", endpointId, Payload())
        };
        var duration = new[] { faultEnd, clearAt, resetAt, invokeAt }.Max() + 10;
        ApplyVisualScenario($"visual-external-{Slug(endpointId)}-{Slug(faultKind)}", seed, start, duration, actions, [],
            $"S5 External：endpoint/fault apply+clear/request invoke/circuit reset；{endpointId} 故障={faultKind}。");
        TrafficExternalStatusText = $"已生成 S5 External 受治理 DSL：{faultKind}。";
        return true;
    }

    private bool TryInt(string text, string name, int min, int max, out int value)
    {
        value = 0;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
            return VisualError($"{name} 必须是 {min}~{max} 的整数。");
        return true;
    }
}
