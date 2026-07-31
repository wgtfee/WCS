namespace Wcs.Simulator.VirtualTraffic;

using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualRgv;

/// <summary>
/// Deterministic, process-local traffic reservation and deadlock model.
/// All state is stored in the S1 SimulationStateStore. Resolution never calls
/// production dispatch, task, route, PLC or vehicle-control components.
/// </summary>
public sealed partial class VirtualTrafficRuntime
{
    private const int IndexChunkSize = 16;
    private const string ZoneIndexName = "zones";
    private const string ReservationIndexName = "reservations";
    private const string RequestIndexName = "requests";
    private const string DeadlockIndexName = "deadlocks";
    private const string OperationSequenceKey = "__vtraffic.operationSequence";
    private const string AuditCountKey = "__vtraffic.audit.count";

    private readonly SimulationStateStore _state;
    private readonly VirtualTrafficOptions _options;
    private readonly VirtualRgvOptions _rgvOptions;

    public VirtualTrafficRuntime(
        SimulationStateStore state,
        VirtualTrafficOptions options,
        VirtualRgvOptions rgvOptions)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _rgvOptions = rgvOptions ?? throw new ArgumentNullException(nameof(rgvOptions));
        _options.Validate();
        _rgvOptions.Validate();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public VirtualTrafficZoneSnapshot DefineZone(
        VirtualTrafficZoneDefinition definition,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var zoneId = NormalizeId(definition.ZoneId, nameof(definition.ZoneId));
        if (_state.Contains(ZoneKey(zoneId)))
            throw new InvalidOperationException($"Virtual traffic zone '{zoneId}' is already defined.");
        if (definition.Capacity is < 1 or > 10_000)
            throw new InvalidOperationException("Virtual traffic zone capacity must be between 1 and 10,000.");
        if (definition.SegmentIds.Count is < 1 || definition.SegmentIds.Count > _options.MaximumSegmentsPerZone)
            throw new InvalidOperationException("Virtual traffic zone segment count is outside MaximumSegmentsPerZone.");

        var segmentIds = definition.SegmentIds
            .Select(id => NormalizeId(id, "SegmentId"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (segmentIds.Length != definition.SegmentIds.Count)
            throw new InvalidOperationException("Virtual traffic zone contains duplicate segments.");

        var rgv = Rgv();
        foreach (var segmentId in segmentIds)
        {
            _ = rgv.GetSegment(segmentId);
            if (_state.Contains(SegmentZoneKey(segmentId)))
                throw new InvalidOperationException($"Virtual RGV segment '{segmentId}' already belongs to a traffic zone.");
        }

        var zoneIds = ReadIndex(ZoneIndexName).ToList();
        if (zoneIds.Count >= _options.MaximumZones)
            throw new InvalidOperationException("Virtual traffic runtime has reached MaximumZones.");

        var stored = new ZoneStorage(zoneId, segmentIds, definition.Capacity, definition.Kind);
        SetJson(ZoneKey(zoneId), stored);
        foreach (var segmentId in segmentIds)
            SetJson(SegmentZoneKey(segmentId), zoneId);
        zoneIds.Add(zoneId);
        WriteIndex(ZoneIndexName, zoneIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "zone.define", zoneId,
            $"kind={definition.Kind};capacity={definition.Capacity};segments={string.Join(',', segmentIds)}", true);
        return ToSnapshot(stored);
    }

    public VirtualTrafficReservationDecision RequestReservation(
        string vehicleId,
        string segmentId,
        int priority,
        long? leaseMilliseconds,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        vehicleId = NormalizeId(vehicleId, nameof(vehicleId));
        segmentId = NormalizeId(segmentId, nameof(segmentId));
        if (priority is < 0 or > 1_000_000)
            throw new InvalidOperationException("Virtual traffic priority must be between 0 and 1,000,000.");
        var lease = leaseMilliseconds ?? _options.DefaultReservationLeaseMilliseconds;
        if (lease is < 1 || lease > _options.MaximumReservationLeaseMilliseconds)
            throw new InvalidOperationException("Virtual traffic reservation lease is outside the configured limit.");

        _ = Rgv().GetVehicle(vehicleId);
        _ = Rgv().GetSegment(segmentId);
        var zone = ReadZoneForSegment(segmentId);
        ExpireReservationsInternal(virtualOffsetMilliseconds);

        var reservationKey = ReservationIdentity(zone.ZoneId, vehicleId);
        if (TryReadJson<ReservationStorage>(ReservationKey(reservationKey), out var existing) &&
            existing.State == VirtualTrafficReservationState.Granted &&
            existing.ExpiresAtOffsetMilliseconds > virtualOffsetMilliseconds)
        {
            var extended = existing with
            {
                SegmentId = segmentId,
                Priority = priority,
                ExpiresAtOffsetMilliseconds = checked(virtualOffsetMilliseconds + lease),
                Version = existing.Version + 1
            };
            SetJson(ReservationKey(reservationKey), extended);
            MarkRequestGranted(zone.ZoneId, vehicleId);
            AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "reservation.extend", vehicleId,
                $"zone={zone.ZoneId};segment={segmentId}", true);
            return new VirtualTrafficReservationDecision(true, zone.ZoneId, segmentId, vehicleId,
                ToSnapshot(extended), null, []);
        }

        var blockers = ActiveReservations(zone.ZoneId, virtualOffsetMilliseconds)
            .Where(item => !string.Equals(item.VehicleId, vehicleId, StringComparison.Ordinal))
            .Select(static item => item.VehicleId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        if (blockers.Length < zone.Capacity)
        {
            var reservation = GrantReservation(zone, vehicleId, segmentId, priority, lease,
                virtualOffsetMilliseconds);
            MarkRequestGranted(zone.ZoneId, vehicleId);
            AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "reservation.grant", vehicleId,
                $"zone={zone.ZoneId};segment={segmentId}", true);
            return new VirtualTrafficReservationDecision(true, zone.ZoneId, segmentId, vehicleId,
                ToSnapshot(reservation), null, []);
        }

        var request = StoreWaitingRequest(zone, vehicleId, segmentId, priority, lease, blockers,
            virtualOffsetMilliseconds);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "reservation.wait", vehicleId,
            $"zone={zone.ZoneId};segment={segmentId};blockers={string.Join(',', blockers)}", true);
        return new VirtualTrafficReservationDecision(false, zone.ZoneId, segmentId, vehicleId,
            null, ToSnapshot(request), blockers);
    }

    public VirtualTrafficRollingReservationResult ReserveRollingWindow(
        string vehicleId,
        int lookAheadSegments,
        int priority,
        long? leaseMilliseconds,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        if (lookAheadSegments is < 1 || lookAheadSegments > _options.MaximumRollingLookAheadSegments)
            throw new InvalidOperationException("Rolling look-ahead is outside MaximumRollingLookAheadSegments.");
        var vehicle = Rgv().GetVehicle(vehicleId);
        if (vehicle.RouteSegmentIds.Count == 0 || vehicle.RouteIndex >= vehicle.RouteSegmentIds.Count)
            return new VirtualTrafficRollingReservationResult(vehicle.VehicleId, [], true);

        var decisions = new List<VirtualTrafficReservationDecision>();
        foreach (var segmentId in vehicle.RouteSegmentIds.Skip(vehicle.RouteIndex).Take(lookAheadSegments))
        {
            var decision = RequestReservation(vehicle.VehicleId, segmentId, priority, leaseMilliseconds,
                virtualOffsetMilliseconds, occurredAtUtc);
            decisions.Add(decision);
            if (!decision.Granted)
                break;
        }

        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "rolling.reserve", vehicle.VehicleId,
            $"requested={decisions.Count};allGranted={decisions.All(static item => item.Granted)}", true);
        return new VirtualTrafficRollingReservationResult(vehicle.VehicleId, decisions,
            decisions.All(static item => item.Granted));
    }

    public IReadOnlyList<string> ReleasePassedReservations(
        string vehicleId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var vehicle = Rgv().GetVehicle(vehicleId);
        var passedSegments = vehicle.RouteSegmentIds.Take(vehicle.RouteIndex).ToHashSet(StringComparer.Ordinal);
        var released = new List<string>();
        foreach (var reservation in ActiveReservations(null, virtualOffsetMilliseconds)
                     .Where(item => string.Equals(item.VehicleId, vehicle.VehicleId, StringComparison.Ordinal) &&
                                    passedSegments.Contains(item.SegmentId)))
        {
            var updated = reservation with
            {
                State = VirtualTrafficReservationState.Released,
                Version = reservation.Version + 1
            };
            SetJson(ReservationKey(reservation.ReservationId), updated);
            released.Add(reservation.ReservationId);
        }
        ReevaluateWaitingRequests(virtualOffsetMilliseconds);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "rolling.release", vehicle.VehicleId,
            string.Join(',', released), true);
        return released;
    }

    public bool ReleaseReservation(
        string vehicleId,
        string segmentId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        vehicleId = NormalizeId(vehicleId, nameof(vehicleId));
        segmentId = NormalizeId(segmentId, nameof(segmentId));
        var zone = ReadZoneForSegment(segmentId);
        var reservationId = ReservationIdentity(zone.ZoneId, vehicleId);
        if (!TryReadJson<ReservationStorage>(ReservationKey(reservationId), out var reservation) ||
            reservation.State != VirtualTrafficReservationState.Granted)
        {
            AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "reservation.release", vehicleId,
                $"zone={zone.ZoneId};missing", false);
            return false;
        }

        SetJson(ReservationKey(reservationId), reservation with
        {
            State = VirtualTrafficReservationState.Released,
            Version = reservation.Version + 1
        });
        ReevaluateWaitingRequests(virtualOffsetMilliseconds);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "reservation.release", vehicleId,
            $"zone={zone.ZoneId};segment={segmentId}", true);
        return true;
    }

    public int ExpireReservations(
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        var expired = ExpireReservationsInternal(virtualOffsetMilliseconds);
        if (expired > 0)
            ReevaluateWaitingRequests(virtualOffsetMilliseconds);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "reservation.expire", "all",
            expired.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
        return expired;
    }

    public IReadOnlyList<VirtualTrafficDeadlockSnapshot> DetectDeadlocks(
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        ExpireReservationsInternal(virtualOffsetMilliseconds);
        ReevaluateWaitingRequests(virtualOffsetMilliseconds);
        var edges = ListWaitEdges();
        var components = FindStronglyConnectedComponents(edges);
        var detected = new List<VirtualTrafficDeadlockSnapshot>();

        foreach (var component in components)
        {
            var componentSet = component.ToHashSet(StringComparer.Ordinal);
            var componentEdges = edges
                .Where(edge => componentSet.Contains(edge.WaitingVehicleId) && componentSet.Contains(edge.BlockingVehicleId))
                .OrderBy(static edge => edge.WaitingVehicleId, StringComparer.Ordinal)
                .ThenBy(static edge => edge.BlockingVehicleId, StringComparer.Ordinal)
                .ThenBy(static edge => edge.ZoneId, StringComparer.Ordinal)
                .ToArray();
            if (component.Count == 1 && !componentEdges.Any(edge =>
                    string.Equals(edge.WaitingVehicleId, edge.BlockingVehicleId, StringComparison.Ordinal)))
                continue;

            var deadlockIds = ReadIndex(DeadlockIndexName).ToList();
            if (deadlockIds.Count >= _options.MaximumDeadlocks)
                throw new InvalidOperationException("Virtual traffic runtime has reached MaximumDeadlocks.");
            var sequence = ReadInt64(OperationSequenceKey) + detected.Count + 1;
            var deadlockId = $"DL-{sequence:D12}";
            var victim = SelectVictim(component);
            var stored = new DeadlockStorage(deadlockId, component.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
                componentEdges, victim, virtualOffsetMilliseconds, false, null, 1);
            SetJson(DeadlockKey(deadlockId), stored);
            deadlockIds.Add(deadlockId);
            WriteIndex(DeadlockIndexName, deadlockIds);
            detected.Add(ToSnapshot(stored));
        }

        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "deadlock.detect", "all",
            $"count={detected.Count}", true);
        return detected;
    }

    public VirtualTrafficResolutionResult ResolveDeadlock(
        string deadlockId,
        long virtualOffsetMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        deadlockId = NormalizeId(deadlockId, nameof(deadlockId));
        var deadlock = ReadRequiredDeadlock(deadlockId);
        if (deadlock.Resolved)
            throw new InvalidOperationException($"Virtual traffic deadlock '{deadlockId}' is already resolved.");

        var released = new List<string>();
        foreach (var reservation in ActiveReservations(null, virtualOffsetMilliseconds)
                     .Where(item => string.Equals(item.VehicleId, deadlock.VictimVehicleId, StringComparison.Ordinal)))
        {
            SetJson(ReservationKey(reservation.ReservationId), reservation with
            {
                State = VirtualTrafficReservationState.Released,
                Version = reservation.Version + 1
            });
            released.Add(reservation.ReservationId);
        }

        var cancelled = new List<string>();
        foreach (var request in ListWaitingRequests(activeOnly: true)
                     .Where(item => string.Equals(item.VehicleId, deadlock.VictimVehicleId, StringComparison.Ordinal)))
        {
            var stored = ReadRequiredRequest(request.RequestId);
            SetJson(RequestKey(request.RequestId), stored with
            {
                State = VirtualTrafficRequestState.Cancelled,
                BlockingVehicleIds = [],
                Version = stored.Version + 1
            });
            cancelled.Add(request.RequestId);
        }

        var waitingBefore = ListWaitingRequests(activeOnly: true).Select(static item => item.RequestId)
            .ToHashSet(StringComparer.Ordinal);
        ReevaluateWaitingRequests(virtualOffsetMilliseconds);
        var newlyGranted = ListWaitingRequests(activeOnly: false)
            .Where(item => waitingBefore.Contains(item.RequestId) && item.State == VirtualTrafficRequestState.Granted)
            .Select(static item => item.RequestId)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        var resolved = deadlock with
        {
            Resolved = true,
            ResolvedAtOffsetMilliseconds = virtualOffsetMilliseconds,
            Version = deadlock.Version + 1
        };
        SetJson(DeadlockKey(deadlockId), resolved);
        AppendAudit(occurredAtUtc, virtualOffsetMilliseconds, "deadlock.resolve", deadlockId,
            $"victim={deadlock.VictimVehicleId};released={released.Count};cancelled={cancelled.Count};granted={newlyGranted.Length}", true);
        return new VirtualTrafficResolutionResult(deadlockId, deadlock.VictimVehicleId,
            released.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            cancelled.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            newlyGranted, ToSnapshot(resolved));
    }

    public IReadOnlyList<VirtualTrafficZoneSnapshot> ListZones() =>
        ReadIndex(ZoneIndexName).Select(ReadRequiredZone).Select(ToSnapshot).ToArray();

    public VirtualTrafficZoneSnapshot GetZone(string zoneId) => ToSnapshot(ReadRequiredZone(zoneId));

    public IReadOnlyList<VirtualTrafficReservationSnapshot> ListReservations(bool activeOnly = true, long virtualOffsetMilliseconds = long.MaxValue) =>
        ReadIndex(ReservationIndexName)
            .Select(ReadRequiredReservation)
            .Where(item => !activeOnly || item.State == VirtualTrafficReservationState.Granted &&
                item.ExpiresAtOffsetMilliseconds > virtualOffsetMilliseconds)
            .Select(ToSnapshot)
            .OrderBy(static item => item.ZoneId, StringComparer.Ordinal)
            .ThenBy(static item => item.VehicleId, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<VirtualTrafficWaitingRequestSnapshot> ListWaitingRequests(bool activeOnly = true) =>
        ReadIndex(RequestIndexName)
            .Select(ReadRequiredRequest)
            .Where(item => !activeOnly || item.State == VirtualTrafficRequestState.Waiting)
            .Select(ToSnapshot)
            .OrderBy(static item => item.Priority)
            .ThenBy(static item => item.Sequence)
            .ThenBy(static item => item.VehicleId, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<VirtualTrafficWaitEdge> ListWaitEdges() =>
        ListWaitingRequests(activeOnly: true)
            .SelectMany(request => request.BlockingVehicleIds.Select(blocker => new VirtualTrafficWaitEdge(
                request.VehicleId, blocker, request.ZoneId, request.SegmentId, request.RequestId)))
            .OrderBy(static edge => edge.WaitingVehicleId, StringComparer.Ordinal)
            .ThenBy(static edge => edge.BlockingVehicleId, StringComparer.Ordinal)
            .ThenBy(static edge => edge.ZoneId, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<VirtualTrafficDeadlockSnapshot> ListDeadlocks(bool activeOnly = true) =>
        ReadIndex(DeadlockIndexName)
            .Select(ReadRequiredDeadlock)
            .Where(item => !activeOnly || !item.Resolved)
            .Select(ToSnapshot)
            .OrderBy(static item => item.DetectedAtOffsetMilliseconds)
            .ThenBy(static item => item.DeadlockId, StringComparer.Ordinal)
            .ToArray();

    public VirtualTrafficDeadlockSnapshot GetDeadlock(string deadlockId) => ToSnapshot(ReadRequiredDeadlock(deadlockId));

    public IReadOnlyList<VirtualTrafficAuditRecord> ListAudit(int take = 100)
    {
        if (take < 1)
            return [];
        var sequence = ReadInt64(OperationSequenceKey);
        var count = (int)Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords);
        var wanted = Math.Min(take, count);
        var records = new List<VirtualTrafficAuditRecord>(wanted);
        for (var offset = 0; offset < wanted; offset++)
        {
            var expectedSequence = sequence - offset;
            var slot = (int)((expectedSequence - 1) % _options.MaximumAuditRecords);
            if (TryReadJson<VirtualTrafficAuditRecord>(AuditSlotKey(slot), out var record) && record.Sequence == expectedSequence)
                records.Add(record);
        }
        return records;
    }

    public VirtualTrafficStatus GetStatus(long virtualOffsetMilliseconds)
    {
        var reservations = ActiveReservations(null, virtualOffsetMilliseconds);
        var requests = ListWaitingRequests(activeOnly: true);
        var edges = ListWaitEdges();
        return new VirtualTrafficStatus(
            ReadIndex(ZoneIndexName).Count,
            reservations.Count,
            requests.Count,
            edges.Count,
            ListDeadlocks(activeOnly: true).Count,
            (int)Math.Min(ReadInt64(AuditCountKey), _options.MaximumAuditRecords),
            ReadInt64(OperationSequenceKey));
    }

    private ReservationStorage GrantReservation(
        ZoneStorage zone,
        string vehicleId,
        string segmentId,
        int priority,
        long leaseMilliseconds,
        long virtualOffsetMilliseconds)
    {
        var reservationId = ReservationIdentity(zone.ZoneId, vehicleId);
        var reservationIds = ReadIndex(ReservationIndexName).ToList();
        var exists = _state.Contains(ReservationKey(reservationId));
        if (!exists && reservationIds.Count >= _options.MaximumReservations)
            throw new InvalidOperationException("Virtual traffic runtime has reached MaximumReservations.");
        var version = exists ? ReadRequiredReservation(reservationId).Version + 1 : 1;
        var stored = new ReservationStorage(reservationId, zone.ZoneId, segmentId, vehicleId, priority,
            VirtualTrafficReservationState.Granted, virtualOffsetMilliseconds,
            checked(virtualOffsetMilliseconds + leaseMilliseconds), version);
        SetJson(ReservationKey(reservationId), stored);
        if (!exists)
        {
            reservationIds.Add(reservationId);
            WriteIndex(ReservationIndexName, reservationIds);
        }
        return stored;
    }

    private RequestStorage StoreWaitingRequest(
        ZoneStorage zone,
        string vehicleId,
        string segmentId,
        int priority,
        long leaseMilliseconds,
        IReadOnlyList<string> blockers,
        long virtualOffsetMilliseconds)
    {
        var requestId = RequestIdentity(zone.ZoneId, vehicleId);
        var requestIds = ReadIndex(RequestIndexName).ToList();
        var exists = _state.Contains(RequestKey(requestId));
        if (!exists && requestIds.Count >= _options.MaximumWaitingRequests)
            throw new InvalidOperationException("Virtual traffic runtime has reached MaximumWaitingRequests.");
        var sequence = exists ? ReadRequiredRequest(requestId).Sequence : ReadInt64(OperationSequenceKey) + 1;
        var version = exists ? ReadRequiredRequest(requestId).Version + 1 : 1;
        var stored = new RequestStorage(requestId, zone.ZoneId, segmentId, vehicleId, priority,
            VirtualTrafficRequestState.Waiting, blockers, virtualOffsetMilliseconds, leaseMilliseconds, sequence, version);
        SetJson(RequestKey(requestId), stored);
        if (!exists)
        {
            requestIds.Add(requestId);
            WriteIndex(RequestIndexName, requestIds);
        }
        return stored;
    }

    private void MarkRequestGranted(string zoneId, string vehicleId)
    {
        var requestId = RequestIdentity(zoneId, vehicleId);
        if (!TryReadJson<RequestStorage>(RequestKey(requestId), out var request))
            return;
        SetJson(RequestKey(requestId), request with
        {
            State = VirtualTrafficRequestState.Granted,
            BlockingVehicleIds = [],
            Version = request.Version + 1
        });
    }

    private int ExpireReservationsInternal(long virtualOffsetMilliseconds)
    {
        var expired = 0;
        foreach (var reservation in ReadIndex(ReservationIndexName).Select(ReadRequiredReservation)
                     .Where(item => item.State == VirtualTrafficReservationState.Granted &&
                                    item.ExpiresAtOffsetMilliseconds <= virtualOffsetMilliseconds))
        {
            SetJson(ReservationKey(reservation.ReservationId), reservation with
            {
                State = VirtualTrafficReservationState.Expired,
                Version = reservation.Version + 1
            });
            expired++;
        }
        return expired;
    }

    private void ReevaluateWaitingRequests(long virtualOffsetMilliseconds)
    {
        foreach (var request in ReadIndex(RequestIndexName).Select(ReadRequiredRequest)
                     .Where(static item => item.State == VirtualTrafficRequestState.Waiting)
                     .OrderBy(static item => item.Priority)
                     .ThenBy(static item => item.Sequence)
                     .ThenBy(static item => item.VehicleId, StringComparer.Ordinal))
        {
            var zone = ReadRequiredZone(request.ZoneId);
            var blockers = ActiveReservations(zone.ZoneId, virtualOffsetMilliseconds)
                .Where(item => !string.Equals(item.VehicleId, request.VehicleId, StringComparison.Ordinal))
                .Select(static item => item.VehicleId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();
            if (blockers.Length < zone.Capacity)
            {
                _ = GrantReservation(zone, request.VehicleId, request.SegmentId, request.Priority,
                    request.LeaseMilliseconds, virtualOffsetMilliseconds);
                SetJson(RequestKey(request.RequestId), request with
                {
                    State = VirtualTrafficRequestState.Granted,
                    BlockingVehicleIds = [],
                    Version = request.Version + 1
                });
            }
            else
            {
                SetJson(RequestKey(request.RequestId), request with
                {
                    BlockingVehicleIds = blockers,
                    Version = request.Version + 1
                });
            }
        }
    }

    private IReadOnlyList<ReservationStorage> ActiveReservations(string? zoneId, long virtualOffsetMilliseconds) =>
        ReadIndex(ReservationIndexName)
            .Select(ReadRequiredReservation)
            .Where(item => item.State == VirtualTrafficReservationState.Granted &&
                           item.ExpiresAtOffsetMilliseconds > virtualOffsetMilliseconds &&
                           (zoneId is null || string.Equals(item.ZoneId, zoneId, StringComparison.Ordinal)))
            .OrderBy(static item => item.ZoneId, StringComparer.Ordinal)
            .ThenBy(static item => item.VehicleId, StringComparer.Ordinal)
            .ToArray();

    private string SelectVictim(IReadOnlyList<string> vehicleIds)
    {
        var requests = ReadIndex(RequestIndexName).Select(ReadRequiredRequest)
            .Where(item => item.State == VirtualTrafficRequestState.Waiting && vehicleIds.Contains(item.VehicleId, StringComparer.Ordinal))
            .ToArray();
        if (requests.Length == 0)
            return vehicleIds.OrderByDescending(static id => id, StringComparer.Ordinal).First();
        return requests
            .OrderByDescending(static item => item.Priority)
            .ThenByDescending(static item => item.Sequence)
            .ThenByDescending(static item => item.VehicleId, StringComparer.Ordinal)
            .First().VehicleId;
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindStronglyConnectedComponents(
        IReadOnlyList<VirtualTrafficWaitEdge> edges)
    {
        var adjacency = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!adjacency.TryGetValue(edge.WaitingVehicleId, out var targets))
                adjacency[edge.WaitingVehicleId] = targets = new SortedSet<string>(StringComparer.Ordinal);
            targets.Add(edge.BlockingVehicleId);
            adjacency.TryAdd(edge.BlockingVehicleId, new SortedSet<string>(StringComparer.Ordinal));
        }

        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IReadOnlyList<string>>();

        void Visit(string node)
        {
            indexes[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var target in adjacency[node])
            {
                if (!indexes.ContainsKey(target))
                {
                    Visit(target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indexes[target]);
                }
            }

            if (lowLinks[node] != indexes[node])
                return;
            var component = new List<string>();
            string item;
            do
            {
                item = stack.Pop();
                onStack.Remove(item);
                component.Add(item);
            } while (!string.Equals(item, node, StringComparison.Ordinal));
            result.Add(component.OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        }

        foreach (var node in adjacency.Keys)
        {
            if (!indexes.ContainsKey(node))
                Visit(node);
        }
        return result.OrderBy(static component => component[0], StringComparer.Ordinal).ToArray();
    }

    private VirtualRgvRuntime Rgv() => new(_state, _rgvOptions);

    private ZoneStorage ReadZoneForSegment(string segmentId)
    {
        if (!TryReadJson<string>(SegmentZoneKey(segmentId), out var zoneId) || string.IsNullOrWhiteSpace(zoneId))
            throw new InvalidOperationException($"Virtual RGV segment '{segmentId}' is not assigned to a traffic zone.");
        return ReadRequiredZone(zoneId);
    }

    private ZoneStorage ReadRequiredZone(string zoneId) =>
        TryReadJson<ZoneStorage>(ZoneKey(NormalizeId(zoneId, nameof(zoneId))), out var value)
            ? value
            : throw new KeyNotFoundException($"Virtual traffic zone '{zoneId}' was not found.");

    private ReservationStorage ReadRequiredReservation(string reservationId) =>
        TryReadJson<ReservationStorage>(ReservationKey(reservationId), out var value)
            ? value
            : throw new KeyNotFoundException($"Virtual traffic reservation '{reservationId}' was not found.");

    private RequestStorage ReadRequiredRequest(string requestId) =>
        TryReadJson<RequestStorage>(RequestKey(requestId), out var value)
            ? value
            : throw new KeyNotFoundException($"Virtual traffic request '{requestId}' was not found.");

    private DeadlockStorage ReadRequiredDeadlock(string deadlockId) =>
        TryReadJson<DeadlockStorage>(DeadlockKey(NormalizeId(deadlockId, nameof(deadlockId))), out var value)
            ? value
            : throw new KeyNotFoundException($"Virtual traffic deadlock '{deadlockId}' was not found.");

    private void AppendAudit(
        DateTimeOffset occurredAtUtc,
        long virtualOffsetMilliseconds,
        string operation,
        string target,
        string? detail,
        bool success)
    {
        var sequence = _state.Increment(OperationSequenceKey, 1);
        var slot = (int)((sequence - 1) % _options.MaximumAuditRecords);
        SetJson(AuditSlotKey(slot), new VirtualTrafficAuditRecord(sequence, occurredAtUtc,
            virtualOffsetMilliseconds, operation, target, detail, success));
        _state.Increment(AuditCountKey, 1);
    }

    private IReadOnlyList<string> ReadIndex(string name)
    {
        var count = (int)ReadInt64(IndexCountKey(name));
        if (count == 0)
            return [];
        var result = new List<string>(count);
        var chunks = (count + IndexChunkSize - 1) / IndexChunkSize;
        for (var index = 0; index < chunks; index++)
        {
            if (TryReadJson<string[]>(IndexChunkKey(name, index), out var values))
                result.AddRange(values);
        }
        if (result.Count != count)
            throw new InvalidOperationException($"Virtual traffic index '{name}' is inconsistent.");
        return result;
    }

    private void WriteIndex(string name, IReadOnlyList<string> values)
    {
        SetJson(IndexCountKey(name), values.Count);
        for (var offset = 0; offset < values.Count; offset += IndexChunkSize)
            SetJson(IndexChunkKey(name, offset / IndexChunkSize), values.Skip(offset).Take(IndexChunkSize).ToArray());
    }

    private long ReadInt64(string key)
    {
        if (!_state.TryGet(key, out var value))
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new InvalidOperationException($"Virtual traffic counter '{key}' is invalid.");
        return number;
    }

    private void SetJson<T>(string key, T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        _state.Set(key, document.RootElement);
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

    private static string NormalizeId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value))
            throw new InvalidOperationException($"Virtual traffic {name} contains unsupported characters.");
        return value;
    }

    private static string ReservationIdentity(string zoneId, string vehicleId) => $"RSV-{zoneId}-{vehicleId}";
    private static string RequestIdentity(string zoneId, string vehicleId) => $"REQ-{zoneId}-{vehicleId}";
    private static string ZoneKey(string id) => $"__vtraffic.zone.{id}";
    private static string SegmentZoneKey(string id) => $"__vtraffic.segmentZone.{id}";
    private static string ReservationKey(string id) => $"__vtraffic.reservation.{id}";
    private static string RequestKey(string id) => $"__vtraffic.request.{id}";
    private static string DeadlockKey(string id) => $"__vtraffic.deadlock.{id}";
    private static string AuditSlotKey(int slot) => $"__vtraffic.audit.{slot:D6}";
    private static string IndexCountKey(string name) => $"__vtraffic.index.{name}.count";
    private static string IndexChunkKey(string name, int index) => $"__vtraffic.index.{name}.{index:D6}";

    private static VirtualTrafficZoneSnapshot ToSnapshot(ZoneStorage value) =>
        new(value.ZoneId, value.SegmentIds, value.Capacity, value.Kind);
    private static VirtualTrafficReservationSnapshot ToSnapshot(ReservationStorage value) =>
        new(value.ReservationId, value.ZoneId, value.SegmentId, value.VehicleId, value.Priority,
            value.State, value.GrantedAtOffsetMilliseconds, value.ExpiresAtOffsetMilliseconds, value.Version);
    private static VirtualTrafficWaitingRequestSnapshot ToSnapshot(RequestStorage value) =>
        new(value.RequestId, value.ZoneId, value.SegmentId, value.VehicleId, value.Priority,
            value.State, value.BlockingVehicleIds, value.RequestedAtOffsetMilliseconds,
            value.LeaseMilliseconds, value.Sequence, value.Version);
    private static VirtualTrafficDeadlockSnapshot ToSnapshot(DeadlockStorage value) =>
        new(value.DeadlockId, value.VehicleIds, value.Edges, value.VictimVehicleId,
            value.DetectedAtOffsetMilliseconds, value.Resolved, value.ResolvedAtOffsetMilliseconds, value.Version);

    private sealed record ZoneStorage(
        string ZoneId,
        IReadOnlyList<string> SegmentIds,
        int Capacity,
        VirtualTrafficZoneKind Kind);

    private sealed record ReservationStorage(
        string ReservationId,
        string ZoneId,
        string SegmentId,
        string VehicleId,
        int Priority,
        VirtualTrafficReservationState State,
        long GrantedAtOffsetMilliseconds,
        long ExpiresAtOffsetMilliseconds,
        long Version);

    private sealed record RequestStorage(
        string RequestId,
        string ZoneId,
        string SegmentId,
        string VehicleId,
        int Priority,
        VirtualTrafficRequestState State,
        IReadOnlyList<string> BlockingVehicleIds,
        long RequestedAtOffsetMilliseconds,
        long LeaseMilliseconds,
        long Sequence,
        long Version);

    private sealed record DeadlockStorage(
        string DeadlockId,
        IReadOnlyList<string> VehicleIds,
        IReadOnlyList<VirtualTrafficWaitEdge> Edges,
        string VictimVehicleId,
        long DetectedAtOffsetMilliseconds,
        bool Resolved,
        long? ResolvedAtOffsetMilliseconds,
        long Version);
}
