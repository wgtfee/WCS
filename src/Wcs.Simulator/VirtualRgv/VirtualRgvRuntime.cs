namespace Wcs.Simulator.VirtualRgv;

using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Core.TransportScheduling;
using Wcs.Simulator.ScenarioEngine;

/// <summary>
/// Deterministic, process-local RGV and segment model backed entirely by the
/// S1 scenario state store. S3 models explicit motion only; it does not select
/// routes, acquire traffic resources, dispatch vehicles or call real drivers.
/// </summary>
public sealed partial class VirtualRgvRuntime
{
    private const int IndexChunkSize = 16;
    private const string VehicleIndexName = "vehicles";
    private const string SegmentIndexName = "segments";
    private const string OperationSequenceKey = "__vrgv.operationSequence";
    private const string AuditCountKey = "__vrgv.audit.count";

    private readonly SimulationStateStore _state;
    private readonly VirtualRgvOptions _options;

    public VirtualRgvRuntime(SimulationStateStore state, VirtualRgvOptions options)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public VirtualRgvSegmentSnapshot DefineSegment(
        VirtualRgvSegmentDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var segmentId = NormalizeId(definition.SegmentId, "SegmentId");
        var fromNodeId = NormalizeId(definition.FromNodeId, "FromNodeId");
        var toNodeId = NormalizeId(definition.ToNodeId, "ToNodeId");
        if (string.Equals(fromNodeId, toNodeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Virtual RGV segment FromNodeId and ToNodeId must be different.");
        if (definition.LengthMillimeters is < 1 || definition.LengthMillimeters > _options.MaximumSegmentLengthMillimeters)
            throw new InvalidOperationException("Virtual RGV segment length is outside the configured limit.");
        if (definition.SpeedLimitMillimetersPerSecond is < 1 || definition.SpeedLimitMillimetersPerSecond > _options.MaximumSpeedMillimetersPerSecond)
            throw new InvalidOperationException("Virtual RGV segment speed limit is outside the configured limit.");
        if (_state.Contains(SegmentKey(segmentId)))
            throw new InvalidOperationException($"Virtual RGV segment '{segmentId}' is already defined.");

        var segmentIds = ReadIndex(SegmentIndexName).ToList();
        if (segmentIds.Count >= _options.MaximumSegments)
            throw new InvalidOperationException("Virtual RGV runtime has reached MaximumSegments.");

        var stored = new SegmentStorage(
            segmentId,
            fromNodeId,
            toNodeId,
            definition.LengthMillimeters,
            definition.SpeedLimitMillimetersPerSecond,
            definition.Enabled);
        SetJson(SegmentKey(segmentId), stored);
        segmentIds.Add(segmentId);
        WriteIndex(SegmentIndexName, segmentIds.OrderBy(static value => value, StringComparer.Ordinal).ToArray());
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "segment.define", segmentId, $"{fromNodeId}->{toNodeId}", true);
        return ToSnapshot(stored);
    }

    public VirtualRgvVehicleSnapshot DefineVehicle(
        VirtualRgvVehicleDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var vehicleId = NormalizeId(definition.VehicleId, "VehicleId");
        var initialNodeId = NormalizeId(definition.InitialNodeId, "InitialNodeId");
        if (definition.SpeedMillimetersPerSecond is < 1 || definition.SpeedMillimetersPerSecond > _options.MaximumSpeedMillimetersPerSecond)
            throw new InvalidOperationException("Virtual RGV vehicle speed is outside the configured limit.");
        if (definition.BatteryPercent is < 0 or > 100)
            throw new InvalidOperationException("Virtual RGV vehicle battery must be between 0 and 100.");
        if (_state.Contains(VehicleKey(vehicleId)))
            throw new InvalidOperationException($"Virtual RGV vehicle '{vehicleId}' is already defined.");

        var vehicleIds = ReadIndex(VehicleIndexName).ToList();
        if (vehicleIds.Count >= _options.MaximumVehicles)
            throw new InvalidOperationException("Virtual RGV runtime has reached MaximumVehicles.");

        var stored = new VehicleStorage(
            vehicleId,
            definition.IsOnline ? TransportVehicleOperatingState.Idle : TransportVehicleOperatingState.Offline,
            definition.IsOnline,
            initialNodeId,
            null,
            0,
            0,
            [],
            0,
            definition.SpeedMillimetersPerSecond,
            definition.BatteryPercent * 100,
            0,
            definition.LoadId,
            definition.Capabilities,
            1,
            virtualOffsetMilliseconds);
        WriteVehicle(stored);
        vehicleIds.Add(vehicleId);
        WriteIndex(VehicleIndexName, vehicleIds.OrderBy(static value => value, StringComparer.Ordinal).ToArray());
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "vehicle.define", vehicleId, initialNodeId, true);
        return ToSnapshot(stored);
    }

    public VirtualRgvVehicleSnapshot AssignRoute(
        string vehicleId,
        IReadOnlyList<string> segmentIds,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        var vehicle = ReadRequiredVehicle(vehicleId);
        if (!vehicle.IsOnline || vehicle.State != TransportVehicleOperatingState.Idle || vehicle.CurrentSegmentId is not null)
            throw new InvalidOperationException("Virtual RGV route can only be assigned to an online idle vehicle at a node.");
        if (segmentIds.Count is < 1 || segmentIds.Count > _options.MaximumRouteSegments)
            throw new InvalidOperationException("Virtual RGV route segment count is outside MaximumRouteSegments.");

        var normalized = segmentIds.Select(id => NormalizeId(id, "SegmentId")).ToArray();
        var segments = normalized.Select(ReadRequiredSegment).ToArray();
        if (segments.Any(static segment => !segment.Enabled))
            throw new InvalidOperationException("Virtual RGV route contains a disabled segment.");
        if (!string.Equals(vehicle.CurrentNodeId, segments[0].FromNodeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Virtual RGV route does not start at the vehicle current node.");
        for (var index = 1; index < segments.Length; index++)
        {
            if (!string.Equals(segments[index - 1].ToNodeId, segments[index].FromNodeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Virtual RGV route segments are not topologically continuous.");
        }

        var updated = vehicle with
        {
            State = TransportVehicleOperatingState.Executing,
            RouteSegmentIds = normalized,
            RouteIndex = 0,
            SegmentProgressMillimeters = 0,
            SegmentElapsedMilliseconds = 0,
            Version = vehicle.Version + 1,
            LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds
        };
        WriteVehicle(updated);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "route.assign", vehicle.VehicleId, string.Join(",", normalized), true);
        return ToSnapshot(updated);
    }

    public VirtualRgvAdvanceResult AdvanceVehicle(
        string vehicleId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var vehicle = ReadRequiredVehicle(vehicleId);
        if (virtualOffsetMilliseconds < vehicle.LastUpdatedOffsetMilliseconds)
            throw new InvalidOperationException("Virtual RGV time cannot move backwards.");

        var fromOffset = vehicle.LastUpdatedOffsetMilliseconds;
        var remainingMilliseconds = virtualOffsetMilliseconds - fromOffset;
        if (!vehicle.IsOnline || vehicle.State != TransportVehicleOperatingState.Executing || vehicle.RouteSegmentIds.Count == 0)
        {
            var unchanged = vehicle with
            {
                LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds,
                Version = vehicle.Version + 1
            };
            WriteVehicle(unchanged);
            AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "vehicle.advance", vehicle.VehicleId, "no-motion", true);
            return new VirtualRgvAdvanceResult(vehicle.VehicleId, fromOffset, virtualOffsetMilliseconds, 0, [], ToSnapshot(unchanged));
        }

        long distanceMoved = 0;
        var completed = new List<string>();
        var current = vehicle;
        while (remainingMilliseconds > 0 && current.RouteIndex < current.RouteSegmentIds.Count)
        {
            var segment = ReadRequiredSegment(current.RouteSegmentIds[current.RouteIndex]);
            if (!segment.Enabled)
                throw new InvalidOperationException($"Virtual RGV segment '{segment.SegmentId}' is disabled during movement.");

            if (current.CurrentSegmentId is null)
            {
                if (!string.Equals(current.CurrentNodeId, segment.FromNodeId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Virtual RGV vehicle position is inconsistent with the assigned route.");
                current = current with
                {
                    CurrentNodeId = null,
                    CurrentSegmentId = segment.SegmentId,
                    SegmentProgressMillimeters = 0,
                    SegmentElapsedMilliseconds = 0
                };
            }

            var speed = Math.Min(current.SpeedMillimetersPerSecond, segment.SpeedLimitMillimetersPerSecond);
            var durationMilliseconds = CeilingDivide((long)segment.LengthMillimeters * 1_000L, speed);
            var requiredMilliseconds = Math.Max(0, durationMilliseconds - current.SegmentElapsedMilliseconds);
            var consumedMilliseconds = Math.Min(remainingMilliseconds, requiredMilliseconds);
            var oldProgress = current.SegmentProgressMillimeters;
            var newElapsed = current.SegmentElapsedMilliseconds + consumedMilliseconds;
            var newProgress = newElapsed >= durationMilliseconds
                ? segment.LengthMillimeters
                : (int)Math.Min(segment.LengthMillimeters, newElapsed * speed / 1_000L);
            distanceMoved += Math.Max(0, newProgress - oldProgress);
            remainingMilliseconds -= consumedMilliseconds;

            if (newElapsed >= durationMilliseconds)
            {
                completed.Add(segment.SegmentId);
                current = current with
                {
                    CurrentNodeId = segment.ToNodeId,
                    CurrentSegmentId = null,
                    SegmentProgressMillimeters = 0,
                    SegmentElapsedMilliseconds = 0,
                    RouteIndex = current.RouteIndex + 1
                };
                if (current.RouteIndex >= current.RouteSegmentIds.Count)
                    current = current with { State = TransportVehicleOperatingState.Idle };
            }
            else
            {
                current = current with
                {
                    SegmentProgressMillimeters = newProgress,
                    SegmentElapsedMilliseconds = newElapsed
                };
            }
        }

        var batteryNumerator = checked(current.BatteryDrainRemainder + distanceMoved * _options.BatteryDrainBasisPointsPerMeter);
        var batteryDrain = batteryNumerator / 1_000L;
        var batteryRemainder = batteryNumerator % 1_000L;
        current = current with
        {
            BatteryBasisPoints = (int)Math.Max(0, current.BatteryBasisPoints - batteryDrain),
            BatteryDrainRemainder = batteryRemainder,
            Version = current.Version + 1,
            LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds
        };
        WriteVehicle(current);
        AppendAudit(
            occurredAtUtc,
            virtualOffsetMilliseconds,
            "vehicle.advance",
            current.VehicleId,
            $"distanceMm={distanceMoved};completed={string.Join(',', completed)}",
            true);
        return new VirtualRgvAdvanceResult(
            current.VehicleId,
            fromOffset,
            virtualOffsetMilliseconds,
            distanceMoved,
            completed,
            ToSnapshot(current));
    }

    public VirtualRgvVehicleSnapshot SetOnline(
        string vehicleId,
        bool isOnline,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var vehicle = ReadRequiredVehicle(vehicleId);
        var state = isOnline
            ? vehicle.RouteIndex < vehicle.RouteSegmentIds.Count
                ? TransportVehicleOperatingState.Executing
                : TransportVehicleOperatingState.Idle
            : TransportVehicleOperatingState.Offline;
        var updated = vehicle with
        {
            IsOnline = isOnline,
            State = state,
            Version = vehicle.Version + 1,
            LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds
        };
        WriteVehicle(updated);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "vehicle.online.set", vehicle.VehicleId, isOnline.ToString(), true);
        return ToSnapshot(updated);
    }

    public VirtualRgvVehicleSnapshot Load(
        string vehicleId,
        string loadId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var vehicle = ReadRequiredVehicle(vehicleId);
        var normalizedLoadId = NormalizeId(loadId, "LoadId");
        EnsureStationaryAtNode(vehicle);
        if (!string.IsNullOrWhiteSpace(vehicle.LoadId))
            throw new InvalidOperationException("Virtual RGV vehicle already carries a load.");
        var updated = vehicle with
        {
            LoadId = normalizedLoadId,
            Version = vehicle.Version + 1,
            LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds
        };
        WriteVehicle(updated);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "vehicle.load", vehicle.VehicleId, normalizedLoadId, true);
        return ToSnapshot(updated);
    }

    public VirtualRgvVehicleSnapshot Unload(
        string vehicleId,
        string? expectedLoadId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var vehicle = ReadRequiredVehicle(vehicleId);
        EnsureStationaryAtNode(vehicle);
        if (string.IsNullOrWhiteSpace(vehicle.LoadId))
            throw new InvalidOperationException("Virtual RGV vehicle does not carry a load.");
        if (!string.IsNullOrWhiteSpace(expectedLoadId) &&
            !string.Equals(vehicle.LoadId, NormalizeId(expectedLoadId, "LoadId"), StringComparison.Ordinal))
            throw new InvalidOperationException("Virtual RGV load identity does not match the expected load.");
        var unloaded = vehicle.LoadId;
        var updated = vehicle with
        {
            LoadId = null,
            Version = vehicle.Version + 1,
            LastUpdatedOffsetMilliseconds = virtualOffsetMilliseconds
        };
        WriteVehicle(updated);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "vehicle.unload", vehicle.VehicleId, unloaded, true);
        return ToSnapshot(updated);
    }

    public VirtualRgvSegmentSnapshot GetSegment(string segmentId) => ToSnapshot(ReadRequiredSegment(segmentId));

    public IReadOnlyList<VirtualRgvSegmentSnapshot> ListSegments() =>
        ReadIndex(SegmentIndexName).Select(ReadRequiredSegment).Select(ToSnapshot).ToArray();

    public VirtualRgvVehicleSnapshot GetVehicle(string vehicleId) => ToSnapshot(ReadRequiredVehicle(vehicleId));

    public IReadOnlyList<VirtualRgvVehicleSnapshot> ListVehicles() =>
        ReadIndex(VehicleIndexName).Select(ReadRequiredVehicle).Select(ToSnapshot).ToArray();

    public TransportVehicleSnapshot GetTransportSnapshot(string vehicleId, DateTimeOffset occurredAtUtc) =>
        GetVehicle(vehicleId).ToTransportSnapshot(occurredAtUtc);

    public IReadOnlyList<VirtualRgvSegmentOccupancy> ListOccupancy() =>
        ListSegments()
            .Select(segment => new VirtualRgvSegmentOccupancy(
                segment.SegmentId,
                ListVehicles()
                    .Where(vehicle => string.Equals(vehicle.CurrentSegmentId, segment.SegmentId, StringComparison.Ordinal))
                    .Select(vehicle => vehicle.VehicleId)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray()))
            .Where(static occupancy => occupancy.VehicleIds.Count > 0)
            .ToArray();

    public IReadOnlyList<VirtualRgvAuditRecord> ListAudit(int take = 100)
    {
        if (take < 1)
            return [];
        var sequence = ReadInt64(OperationSequenceKey);
        var count = (int)Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords);
        var wanted = Math.Min(take, count);
        var records = new List<VirtualRgvAuditRecord>(wanted);
        for (var offset = 0; offset < wanted; offset++)
        {
            var expectedSequence = sequence - offset;
            var slot = (int)((expectedSequence - 1) % _options.MaximumAuditRecords);
            if (TryReadJson<VirtualRgvAuditRecord>(AuditSlotKey(slot), out var record) && record.Sequence == expectedSequence)
                records.Add(record);
        }
        return records;
    }

    public VirtualRgvStatus GetStatus()
    {
        var vehicles = ListVehicles();
        var occupancy = ListOccupancy();
        return new VirtualRgvStatus(
            vehicles.Count,
            ReadIndex(SegmentIndexName).Count,
            vehicles.Count(static vehicle => vehicle.State == TransportVehicleOperatingState.Executing),
            occupancy.Count,
            (int)Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords),
            ReadInt64(OperationSequenceKey));
    }

    private static void EnsureStationaryAtNode(VehicleStorage vehicle)
    {
        if (!vehicle.IsOnline || vehicle.State != TransportVehicleOperatingState.Idle || vehicle.CurrentSegmentId is not null || string.IsNullOrWhiteSpace(vehicle.CurrentNodeId))
            throw new InvalidOperationException("Virtual RGV load operation requires an online idle vehicle at a node.");
    }

    private SegmentStorage ReadRequiredSegment(string segmentId)
    {
        var normalized = NormalizeId(segmentId, "SegmentId");
        if (!TryReadJson<SegmentStorage>(SegmentKey(normalized), out var segment))
            throw new KeyNotFoundException($"Virtual RGV segment '{normalized}' was not found.");
        return segment;
    }

    private VehicleStorage ReadRequiredVehicle(string vehicleId)
    {
        var normalized = NormalizeId(vehicleId, "VehicleId");
        if (!TryReadJson<VehicleStorage>(VehicleKey(normalized), out var vehicle))
            throw new KeyNotFoundException($"Virtual RGV vehicle '{normalized}' was not found.");
        return vehicle;
    }

    private void WriteVehicle(VehicleStorage vehicle) => SetJson(VehicleKey(vehicle.VehicleId), vehicle);

    private void AppendAudit(
        DateTimeOffset occurredAtUtc,
        long virtualOffsetMilliseconds,
        string operation,
        string target,
        string? detail,
        bool success)
    {
        var sequence = NextOperationSequence();
        var slot = (int)((sequence - 1) % _options.MaximumAuditRecords);
        SetJson(AuditSlotKey(slot), new VirtualRgvAuditRecord(
            sequence,
            occurredAtUtc,
            virtualOffsetMilliseconds,
            operation,
            target,
            detail,
            success));
        SetJson(AuditCountKey, Math.Min(sequence, _options.MaximumAuditRecords));
    }

    private long NextOperationSequence()
    {
        var next = checked(ReadInt64(OperationSequenceKey) + 1);
        SetJson(OperationSequenceKey, next);
        return next;
    }

    private IReadOnlyList<string> ReadIndex(string name)
    {
        var count = (int)ReadInt64(IndexCountKey(name));
        if (count == 0)
            return [];
        var result = new List<string>(count);
        var chunks = CeilingDivide(count, IndexChunkSize);
        for (var index = 0; index < chunks; index++)
        {
            if (!TryReadJson<string[]>(IndexChunkKey(name, index), out var chunk))
                throw new InvalidOperationException($"Virtual RGV index '{name}' is incomplete.");
            result.AddRange(chunk);
        }
        if (result.Count != count)
            throw new InvalidOperationException($"Virtual RGV index '{name}' count is inconsistent.");
        return result;
    }

    private void WriteIndex(string name, IReadOnlyList<string> values)
    {
        SetJson(IndexCountKey(name), values.Count);
        var chunks = CeilingDivide(values.Count, IndexChunkSize);
        for (var index = 0; index < chunks; index++)
        {
            var chunk = values.Skip(index * IndexChunkSize).Take(IndexChunkSize).ToArray();
            SetJson(IndexChunkKey(name, index), chunk);
        }
    }

    private long ReadInt64(string key)
    {
        if (!_state.TryGet(key, out var value))
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new InvalidOperationException($"Virtual RGV state '{key}' is not an Int64.");
        return number;
    }

    private void SetJson<T>(string key, T value) => _state.Set(key, JsonSerializer.SerializeToElement(value));

    private bool TryReadJson<T>(string key, out T value)
    {
        if (_state.TryGet(key, out var element))
        {
            value = element.Deserialize<T>()
                ?? throw new InvalidOperationException($"Virtual RGV state '{key}' is empty.");
            return true;
        }
        value = default!;
        return false;
    }

    private static string NormalizeId(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value))
            throw new InvalidOperationException($"Virtual RGV {fieldName} is required and must use [A-Za-z0-9._-] with a maximum length of 128.");
        return value;
    }

    private static long CeilingDivide(long value, long divisor) => checked((value + divisor - 1) / divisor);
    private static int CeilingDivide(int value, int divisor) => (value + divisor - 1) / divisor;

    private static VirtualRgvSegmentSnapshot ToSnapshot(SegmentStorage segment) => new(
        segment.SegmentId,
        segment.FromNodeId,
        segment.ToNodeId,
        segment.LengthMillimeters,
        segment.SpeedLimitMillimetersPerSecond,
        segment.Enabled);

    private static VirtualRgvVehicleSnapshot ToSnapshot(VehicleStorage vehicle) => new(
        vehicle.VehicleId,
        vehicle.State,
        vehicle.IsOnline,
        vehicle.CurrentNodeId,
        vehicle.CurrentSegmentId,
        vehicle.SegmentProgressMillimeters,
        vehicle.SegmentElapsedMilliseconds,
        vehicle.RouteSegmentIds,
        vehicle.RouteIndex,
        vehicle.SpeedMillimetersPerSecond,
        vehicle.BatteryBasisPoints,
        vehicle.BatteryDrainRemainder,
        vehicle.LoadId,
        vehicle.Capabilities,
        vehicle.Version,
        vehicle.LastUpdatedOffsetMilliseconds);

    private static string SegmentKey(string segmentId) => $"__vrgv.segment.{segmentId}";
    private static string VehicleKey(string vehicleId) => $"__vrgv.vehicle.{vehicleId}";
    private static string IndexCountKey(string name) => $"__vrgv.index.{name}.count";
    private static string IndexChunkKey(string name, int index) => $"__vrgv.index.{name}.{index}";
    private static string AuditSlotKey(int slot) => $"__vrgv.audit.{slot}";

    private sealed record SegmentStorage(
        string SegmentId,
        string FromNodeId,
        string ToNodeId,
        int LengthMillimeters,
        int SpeedLimitMillimetersPerSecond,
        bool Enabled);

    private sealed record VehicleStorage(
        string VehicleId,
        TransportVehicleOperatingState State,
        bool IsOnline,
        string? CurrentNodeId,
        string? CurrentSegmentId,
        int SegmentProgressMillimeters,
        long SegmentElapsedMilliseconds,
        IReadOnlyList<string> RouteSegmentIds,
        int RouteIndex,
        int SpeedMillimetersPerSecond,
        int BatteryBasisPoints,
        long BatteryDrainRemainder,
        string? LoadId,
        TransportVehicleCapability Capabilities,
        long Version,
        long LastUpdatedOffsetMilliseconds);
}
