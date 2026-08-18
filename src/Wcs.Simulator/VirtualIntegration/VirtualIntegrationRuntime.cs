namespace Wcs.Simulator.VirtualIntegration;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;
using Wcs.Simulator.VirtualHealth;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

/// <summary>
/// Simulation-only S7 composition across the existing S2-S6 virtual runtimes.
/// It never calls production PLC, vehicle, dispatch, SQL, HTTP or model services.
/// All mission and subsystem state lives in the shared S1 SimulationStateStore,
/// therefore checkpoint/restore and final-state hashing cover the full mission.
/// </summary>
public sealed partial class VirtualIntegrationRuntime
{
    private const string MissionCountKey = "__vintegration.mission.count";
    private const string OperationSequenceKey = "__vintegration.operationSequence";
    private const string AuditCountKey = "__vintegration.audit.count";

    private readonly SimulationStateStore _state;
    private readonly VirtualIntegrationOptions _options;
    private readonly VirtualPlcOptions _plcOptions;
    private readonly VirtualRgvOptions _rgvOptions;
    private readonly VirtualTrafficOptions _trafficOptions;
    private readonly VirtualExternalOptions _externalOptions;
    private readonly VirtualHealthOptions _healthOptions;
    private readonly ulong _deterministicSalt;

    public VirtualIntegrationRuntime(
        SimulationStateStore state,
        VirtualIntegrationOptions options,
        VirtualPlcOptions plcOptions,
        VirtualRgvOptions rgvOptions,
        VirtualTrafficOptions trafficOptions,
        VirtualExternalOptions externalOptions,
        VirtualHealthOptions healthOptions,
        ulong deterministicSalt = 0)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _plcOptions = plcOptions ?? throw new ArgumentNullException(nameof(plcOptions));
        _rgvOptions = rgvOptions ?? throw new ArgumentNullException(nameof(rgvOptions));
        _trafficOptions = trafficOptions ?? throw new ArgumentNullException(nameof(trafficOptions));
        _externalOptions = externalOptions ?? throw new ArgumentNullException(nameof(externalOptions));
        _healthOptions = healthOptions ?? throw new ArgumentNullException(nameof(healthOptions));
        _deterministicSalt = deterministicSalt;
        _options.Validate();
        _plcOptions.Validate();
        _rgvOptions.Validate();
        _trafficOptions.Validate();
        _externalOptions.Validate();
        _healthOptions.Validate();
        if (_options.ReservationLeaseMilliseconds > _trafficOptions.MaximumReservationLeaseMilliseconds)
            throw new InvalidOperationException("S7 reservation lease exceeds SimulationVirtualTraffic.MaximumReservationLeaseMilliseconds.");
        if (_options.ExternalAckMaximumAttempts > _externalOptions.MaximumRetryAttempts)
            throw new InvalidOperationException("S7 external ack attempts exceed SimulationVirtualExternal.MaximumRetryAttempts.");
        if (_options.ExternalAckTimeoutMilliseconds > _externalOptions.MaximumDelayMilliseconds ||
            _options.ExternalAckRetryDelayMilliseconds > _externalOptions.MaximumDelayMilliseconds)
            throw new InvalidOperationException("S7 external ack timing exceeds SimulationVirtualExternal.MaximumDelayMilliseconds.");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public VirtualIntegrationMissionSnapshot DefineMission(
        VirtualIntegrationMissionDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var missionId = NormalizeId(definition.MissionId, nameof(definition.MissionId));
        if (_state.Contains(MissionKey(missionId)))
            throw new InvalidOperationException($"Virtual integration mission '{missionId}' is already defined.");
        if (ReadInt64(MissionCountKey) >= _options.MaximumMissions)
            throw new InvalidOperationException("Virtual integration runtime has reached MaximumMissions.");
        if (definition.Segments.Count is < 1 || definition.Segments.Count > _options.MaximumSegmentsPerMission)
            throw new InvalidOperationException("Virtual integration mission segment count is outside MaximumSegmentsPerMission.");
        if (definition.Segments.Count > _trafficOptions.MaximumRollingLookAheadSegments)
            throw new InvalidOperationException("S7 requires the complete mission route to fit inside MaximumRollingLookAheadSegments.");
        if (definition.Priority is < 0 or > 1_000_000)
            throw new InvalidOperationException("Virtual integration mission priority must be between 0 and 1,000,000.");

        var segmentIds = definition.Segments.Select(static item => item.SegmentId).ToArray();
        if (segmentIds.Distinct(StringComparer.Ordinal).Count() != segmentIds.Length)
            throw new InvalidOperationException("Virtual integration mission contains duplicate segment ids.");
        if (!string.Equals(definition.Segments[0].FromNodeId, definition.SourceNodeId, StringComparison.Ordinal) ||
            !string.Equals(definition.Segments[^1].ToNodeId, definition.DestinationNodeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Virtual integration route endpoints do not match SourceNodeId/DestinationNodeId.");
        for (var index = 1; index < definition.Segments.Count; index++)
        {
            if (!string.Equals(definition.Segments[index - 1].ToNodeId, definition.Segments[index].FromNodeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Virtual integration route segments are not topologically continuous.");
        }

        var plc = Plc();
        plc.DefineBlock(definition.PlcBlockKey, 8, ReadOnlySpan<byte>.Empty,
            virtualOffsetMilliseconds, occurredAtUtc);

        var rgv = Rgv();
        foreach (var segment in definition.Segments)
        {
            rgv.DefineSegment(new VirtualRgvSegmentDefinition
            {
                SegmentId = segment.SegmentId,
                FromNodeId = segment.FromNodeId,
                ToNodeId = segment.ToNodeId,
                LengthMillimeters = segment.LengthMillimeters,
                SpeedLimitMillimetersPerSecond = segment.SpeedLimitMillimetersPerSecond,
                Enabled = true
            }, virtualOffsetMilliseconds, occurredAtUtc);
        }
        rgv.DefineVehicle(new VirtualRgvVehicleDefinition
        {
            VehicleId = definition.VehicleId,
            InitialNodeId = definition.SourceNodeId,
            SpeedMillimetersPerSecond = definition.VehicleSpeedMillimetersPerSecond,
            BatteryPercent = definition.VehicleBatteryPercent,
            IsOnline = true
        }, virtualOffsetMilliseconds, occurredAtUtc);

        var traffic = Traffic();
        for (var index = 0; index < definition.Segments.Count; index++)
        {
            traffic.DefineZone(new VirtualTrafficZoneDefinition
            {
                ZoneId = ZoneId(missionId, index + 1),
                SegmentIds = [definition.Segments[index].SegmentId],
                Capacity = 1,
                Kind = VirtualTrafficZoneKind.SharedSegment
            }, virtualOffsetMilliseconds, occurredAtUtc);
        }

        External().DefineEndpoint(
            new VirtualExternalEndpointDefinition(definition.ExternalEndpointId, definition.ExternalSystemKind),
            virtualOffsetMilliseconds, occurredAtUtc);
        Health().DefineAsset(
            new VirtualHealthAssetDefinition(definition.HealthAssetId,
                definition.InitialHealthScore,
                definition.InitialFusionRiskScore,
                1),
            virtualOffsetMilliseconds, occurredAtUtc);

        var stored = new MissionStorage(
            missionId,
            VirtualIntegrationMissionState.Defined,
            definition.PlcBlockKey,
            NormalizeId(definition.VehicleId, nameof(definition.VehicleId)),
            NormalizeId(definition.LoadId, nameof(definition.LoadId)),
            NormalizeId(definition.SourceNodeId, nameof(definition.SourceNodeId)),
            NormalizeId(definition.DestinationNodeId, nameof(definition.DestinationNodeId)),
            NormalizeId(definition.ExternalEndpointId, nameof(definition.ExternalEndpointId)),
            NormalizeId(definition.HealthAssetId, nameof(definition.HealthAssetId)),
            segmentIds.Select(id => NormalizeId(id, "SegmentId")).ToArray(),
            definition.Priority,
            virtualOffsetMilliseconds,
            virtualOffsetMilliseconds,
            1);
        SetJson(MissionKey(missionId), stored);
        var count = _state.Increment(MissionCountKey, 1);
        SetJson(MissionIndexKey(count), missionId);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "mission.define", missionId,
            $"segments={segmentIds.Length};vehicle={stored.VehicleId};endpoint={stored.ExternalEndpointId}", true);
        return ToSnapshot(stored);
    }

    public VirtualIntegrationMissionSnapshot DispatchMission(
        string missionId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var mission = ReadRequiredMission(missionId);
        if (mission.State != VirtualIntegrationMissionState.Defined)
            throw new InvalidOperationException("Virtual integration mission can only be dispatched from Defined state.");

        RequirePlcSuccess(Plc().Write(mission.PlcBlockKey, 0, new byte[] { 1 },
            virtualOffsetMilliseconds, occurredAtUtc), "dispatch-request");
        var rgv = Rgv();
        rgv.Load(mission.VehicleId, mission.LoadId, virtualOffsetMilliseconds, occurredAtUtc);
        rgv.AssignRoute(mission.VehicleId, mission.SegmentIds, virtualOffsetMilliseconds, occurredAtUtc);
        var reserved = Traffic().ReserveRollingWindow(
            mission.VehicleId,
            mission.SegmentIds.Count,
            mission.Priority,
            _options.ReservationLeaseMilliseconds,
            virtualOffsetMilliseconds,
            occurredAtUtc);
        if (!reserved.AllGranted)
            throw new InvalidOperationException("Virtual integration mission could not reserve the complete route.");

        var updated = mission with
        {
            State = VirtualIntegrationMissionState.Dispatched,
            LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds,
            Version = mission.Version + 1
        };
        WriteMission(updated);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "mission.dispatch", mission.MissionId,
            $"reservations={reserved.Decisions.Count}", true);
        return ToSnapshot(updated);
    }

    public VirtualIntegrationMissionSnapshot AdvanceMission(
        string missionId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var mission = ReadRequiredMission(missionId);
        if (mission.State is not (VirtualIntegrationMissionState.Dispatched or VirtualIntegrationMissionState.Moving))
            throw new InvalidOperationException("Virtual integration mission can only advance after dispatch and before completion.");

        var rgv = Rgv();
        var result = rgv.AdvanceVehicle(mission.VehicleId, virtualOffsetMilliseconds, occurredAtUtc);
        var traffic = Traffic();
        _ = traffic.ReleasePassedReservations(mission.VehicleId, virtualOffsetMilliseconds, occurredAtUtc);

        var newState = result.Vehicle.RouteCompleted
            ? VirtualIntegrationMissionState.Completed
            : VirtualIntegrationMissionState.Moving;
        if (newState == VirtualIntegrationMissionState.Completed)
        {
            foreach (var segmentId in mission.SegmentIds)
                _ = traffic.ReleaseReservation(mission.VehicleId, segmentId, virtualOffsetMilliseconds, occurredAtUtc);
            if (!string.IsNullOrWhiteSpace(result.Vehicle.LoadId))
                _ = rgv.Unload(mission.VehicleId, mission.LoadId, virtualOffsetMilliseconds, occurredAtUtc);
            RequirePlcSuccess(Plc().Write(mission.PlcBlockKey, 1, new byte[] { 1 },
                virtualOffsetMilliseconds, occurredAtUtc), "transport-completed");
        }

        var updated = mission with
        {
            State = newState,
            LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds,
            Version = mission.Version + 1
        };
        WriteMission(updated);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "mission.advance", mission.MissionId,
            $"state={newState};distanceMm={result.DistanceMovedMillimeters};completed={string.Join(',', result.CompletedSegmentIds)}", true);
        return ToSnapshot(updated);
    }

    public VirtualIntegrationMissionSnapshot AcknowledgeMission(
        string missionId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var mission = ReadRequiredMission(missionId);
        if (mission.State is not (VirtualIntegrationMissionState.Completed or VirtualIntegrationMissionState.Acknowledged))
            throw new InvalidOperationException("Virtual integration mission can only be acknowledged after completion.");

        var request = External().Invoke(
            new VirtualExternalInvokeRequest(
                mission.ExternalEndpointId,
                "Transport.Completed",
                mission.MissionId,
                ComputePayloadHash(mission),
                _options.ExternalAckMaximumAttempts,
                _options.ExternalAckTimeoutMilliseconds,
                _options.ExternalAckRetryDelayMilliseconds),
            virtualOffsetMilliseconds,
            occurredAtUtc);
        if (request.State != VirtualExternalRequestState.Succeeded)
            throw new InvalidOperationException($"Virtual integration external acknowledgement failed with state {request.State}.");

        if (mission.State != VirtualIntegrationMissionState.Acknowledged)
        {
            _ = Health().RecordOutcome(
                mission.HealthAssetId,
                VirtualHealthOutcomeKind.CensoredNoFailure,
                virtualOffsetMilliseconds,
                occurredAtUtc,
                $"mission-{mission.MissionId}-completed");
            RequirePlcSuccess(Plc().Write(mission.PlcBlockKey, 2, new byte[] { 1 },
                virtualOffsetMilliseconds, occurredAtUtc), "external-acknowledged");
            mission = mission with
            {
                State = VirtualIntegrationMissionState.Acknowledged,
                LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds,
                Version = mission.Version + 1
            };
            WriteMission(mission);
        }

        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "mission.ack", mission.MissionId,
            $"request={request.RequestId};idempotentReplay={request.IdempotencyReplayed}", true);
        return ToSnapshot(mission);
    }

    public VirtualIntegrationMissionSnapshot GetMission(string missionId) =>
        ToSnapshot(ReadRequiredMission(missionId));

    public IReadOnlyList<VirtualIntegrationMissionSnapshot> ListMissions() =>
        Enumerable.Range(1, checked((int)ReadInt64(MissionCountKey)))
            .Select(index => ReadRequiredJson<string>(MissionIndexKey(index)))
            .Select(ReadRequiredMission)
            .Select(ToSnapshot)
            .OrderBy(static item => item.MissionId, StringComparer.Ordinal)
            .ToArray();

    public VirtualIntegrationConsistencySnapshot GetConsistency(
        string missionId,
        long virtualOffsetMilliseconds)
    {
        var mission = ReadRequiredMission(missionId);
        var vehicle = Rgv().GetVehicle(mission.VehicleId);
        var traffic = Traffic();
        var activeReservations = traffic.ListReservations(true, virtualOffsetMilliseconds)
            .Where(item => string.Equals(item.VehicleId, mission.VehicleId, StringComparison.Ordinal))
            .ToArray();
        var waiting = traffic.ListWaitingRequests(true)
            .Where(item => string.Equals(item.VehicleId, mission.VehicleId, StringComparison.Ordinal))
            .ToArray();
        var activeDeadlocks = traffic.ListDeadlocks(true)
            .Where(item => item.VehicleIds.Contains(mission.VehicleId, StringComparer.Ordinal))
            .ToArray();
        var requests = External().ListRequests()
            .Where(item => string.Equals(item.EndpointId, mission.ExternalEndpointId, StringComparison.Ordinal) &&
                           string.Equals(item.IdempotencyKey, mission.MissionId, StringComparison.Ordinal))
            .ToArray();
        var outcomes = Health().ListOutcomes(mission.HealthAssetId)
            .Where(item => item.Kind == VirtualHealthOutcomeKind.CensoredNoFailure &&
                           item.Note.Contains(mission.MissionId, StringComparison.Ordinal))
            .ToArray();
        var block = Plc().GetBlock(mission.PlcBlockKey);

        var final = mission.State == VirtualIntegrationMissionState.Acknowledged;
        var vehicleAtDestination = !final || vehicle.IsAtNode &&
            string.Equals(vehicle.CurrentNodeId, mission.DestinationNodeId, StringComparison.Ordinal);
        var vehicleUnloaded = !final || vehicle.LoadId is null;
        var trafficClean = !final || activeReservations.Length == 0 && waiting.Length == 0;
        var externalExactlyOnce = !final || requests.Length == 1;
        var externalSucceeded = !final || requests.Length == 1 && requests[0].State == VirtualExternalRequestState.Succeeded;
        var plcFlags = !final || block.Data.Length >= 3 && block.Data[0] == 1 && block.Data[1] == 1 && block.Data[2] == 1;
        var healthOnce = !final || outcomes.Length == 1;
        var noDeadlock = activeDeadlocks.Length == 0;
        var consistent = vehicleAtDestination && vehicleUnloaded && trafficClean && externalExactlyOnce &&
                         externalSucceeded && plcFlags && healthOnce && noDeadlock;
        var detail = $"vehicleAtDestination={vehicleAtDestination};vehicleUnloaded={vehicleUnloaded};" +
                     $"reservations={activeReservations.Length};waiting={waiting.Length};requests={requests.Length};" +
                     $"outcomes={outcomes.Length};deadlocks={activeDeadlocks.Length};plc={Convert.ToHexString(block.Data.AsSpan(0, Math.Min(3, block.Data.Length)))}";
        return new VirtualIntegrationConsistencySnapshot(
            mission.MissionId,
            mission.State,
            vehicleAtDestination,
            vehicleUnloaded,
            trafficClean,
            externalExactlyOnce,
            externalSucceeded,
            plcFlags,
            healthOnce,
            noDeadlock,
            consistent,
            detail);
    }

    public IReadOnlyList<VirtualIntegrationAuditRecord> ListAudit()
    {
        var total = Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords);
        if (total <= 0)
            return [];
        var sequence = ReadInt64(OperationSequenceKey);
        var first = Math.Max(1, sequence - total + 1);
        var result = new List<VirtualIntegrationAuditRecord>((int)total);
        for (var current = first; current <= sequence; current++)
        {
            var slot = (int)((current - 1) % _options.MaximumAuditRecords);
            if (TryReadJson<VirtualIntegrationAuditRecord>(AuditSlotKey(slot), out var record) && record.Sequence == current)
                result.Add(record);
        }
        return result;
    }

    public VirtualIntegrationStatus GetStatus()
    {
        var missions = ListMissions();
        return new VirtualIntegrationStatus(
            missions.Count,
            missions.Count(static item => item.State == VirtualIntegrationMissionState.Defined),
            missions.Count(static item => item.State is VirtualIntegrationMissionState.Dispatched or VirtualIntegrationMissionState.Moving),
            missions.Count(static item => item.State == VirtualIntegrationMissionState.Completed),
            missions.Count(static item => item.State == VirtualIntegrationMissionState.Acknowledged),
            (int)Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords),
            ReadInt64(OperationSequenceKey));
    }

    private VirtualPlcRuntime Plc() => new(_state, _plcOptions, _deterministicSalt);
    private VirtualRgvRuntime Rgv() => new(_state, _rgvOptions);
    private VirtualTrafficRuntime Traffic() => new(_state, _trafficOptions, _rgvOptions);
    private VirtualExternalRuntime External() => new(_state, _externalOptions);
    private VirtualHealthRuntime Health() => new(_state, _healthOptions);

    private void RequirePlcSuccess(VirtualPlcOperationResult result, string operation)
    {
        if (!result.Success)
            throw new InvalidOperationException($"S7 virtual PLC operation '{operation}' failed: {result.ErrorCode ?? "UNKNOWN"}.");
    }

    private static string ComputePayloadHash(MissionStorage mission)
    {
        var canonical = string.Join('|', mission.MissionId, mission.VehicleId, mission.LoadId,
            mission.SourceNodeId, mission.DestinationNodeId, string.Join(',', mission.SegmentIds));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string ZoneId(string missionId, int sequence) => $"{missionId}-Z{sequence:D3}";

    private static string NormalizeId(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!IdentifierRegex().IsMatch(normalized))
            throw new InvalidOperationException($"Virtual integration {name} contains unsupported characters.");
        return normalized;
    }

    private MissionStorage ReadRequiredMission(string missionId)
    {
        missionId = NormalizeId(missionId, nameof(missionId));
        if (!TryReadJson<MissionStorage>(MissionKey(missionId), out var mission))
            throw new KeyNotFoundException($"Virtual integration mission '{missionId}' was not found.");
        return mission;
    }

    private void WriteMission(MissionStorage mission) => SetJson(MissionKey(mission.MissionId), mission);

    private static VirtualIntegrationMissionSnapshot ToSnapshot(MissionStorage mission) => new(
        mission.MissionId,
        mission.State,
        mission.PlcBlockKey,
        mission.VehicleId,
        mission.LoadId,
        mission.SourceNodeId,
        mission.DestinationNodeId,
        mission.ExternalEndpointId,
        mission.HealthAssetId,
        mission.SegmentIds,
        mission.Priority,
        mission.DefinedAtOffsetMilliseconds,
        mission.LastUpdatedOffsetMilliseconds,
        mission.Version);

    private void AppendAudit(
        DateTimeOffset occurredAtUtc,
        long virtualOffsetMilliseconds,
        string operation,
        string missionId,
        string? detail,
        bool success)
    {
        var sequence = _state.Increment(OperationSequenceKey, 1);
        var slot = (int)((sequence - 1) % _options.MaximumAuditRecords);
        SetJson(AuditSlotKey(slot), new VirtualIntegrationAuditRecord(
            sequence, occurredAtUtc, virtualOffsetMilliseconds, operation, missionId, detail, success));
        var count = Math.Min(_options.MaximumAuditRecords, ReadInt64(AuditCountKey) + 1);
        SetJson(AuditCountKey, count);
    }

    private long ReadInt64(string key)
    {
        if (!_state.TryGet(key, out var value))
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw new InvalidOperationException($"Virtual integration state '{key}' is not an Int64.");
        return result;
    }

    private T ReadRequiredJson<T>(string key)
    {
        if (!TryReadJson<T>(key, out var value))
            throw new KeyNotFoundException($"Virtual integration state '{key}' was not found.");
        return value;
    }

    private bool TryReadJson<T>(string key, out T value)
    {
        if (_state.TryGet(key, out var element))
        {
            var parsed = element.Deserialize<T>();
            if (parsed is not null)
            {
                value = parsed;
                return true;
            }
        }
        value = default!;
        return false;
    }

    private void SetJson<T>(string key, T value) =>
        _state.Set(key, JsonSerializer.SerializeToElement(value));

    private static string MissionKey(string missionId) => $"__vintegration.mission.{missionId}";
    private static string MissionIndexKey(long index) => $"__vintegration.mission.index.{index:D6}";
    private static string AuditSlotKey(int slot) => $"__vintegration.audit.slot.{slot:D6}";

    private sealed record MissionStorage(
        string MissionId,
        VirtualIntegrationMissionState State,
        string PlcBlockKey,
        string VehicleId,
        string LoadId,
        string SourceNodeId,
        string DestinationNodeId,
        string ExternalEndpointId,
        string HealthAssetId,
        IReadOnlyList<string> SegmentIds,
        int Priority,
        long DefinedAtOffsetMilliseconds,
        long LastUpdatedOffsetMilliseconds,
        long Version);
}
