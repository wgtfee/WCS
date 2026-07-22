namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using System.Text.Json;

public interface ITransportProductionTuningService
{
    TransportProductionTuningOptions Current { get; }
    Task<TransportProductionTuningOptions> LoadAsync(CancellationToken cancellationToken = default);
    Task<TransportProductionTuningSaveResult> SaveAsync(
        TransportProductionTuningOptions options,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default);
}

public sealed class TransportProductionTuningService : ITransportProductionTuningService
{
    private const string RecordId = "production-tuning";
    private readonly ITransportJournalStore _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TransportProductionTuningOptions _current = new();

    public TransportProductionTuningService(ITransportJournalStore journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public TransportProductionTuningOptions Current => Volatile.Read(ref _current);

    public async Task<TransportProductionTuningOptions> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ProductionTuning,
            20,
            cancellationToken).ConfigureAwait(false);
        var record = records.FirstOrDefault(x => string.Equals(x.RecordId, RecordId, StringComparison.Ordinal));
        if (record is null)
            return Current;

        var restored = JsonSerializer.Deserialize<TransportProductionTuningOptions>(record.PayloadJson);
        if (restored is not null)
            Volatile.Write(ref _current, restored);
        return Current;
    }

    public async Task<TransportProductionTuningSaveResult> SaveAsync(
        TransportProductionTuningOptions options,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var error = Validate(options);
        if (error is not null)
            return TransportProductionTuningSaveResult.Failed(error);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current.Version != expectedVersion)
                return TransportProductionTuningSaveResult.Conflict(Current);

            var saved = options with
            {
                Version = expectedVersion + 1,
                UpdatedBy = updatedBy,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _journal.UpsertAsync(new TransportJournalRecord
            {
                Category = TransportJournalCategory.ProductionTuning,
                RecordId = RecordId,
                PayloadJson = JsonSerializer.Serialize(saved),
                OccurredAtUtc = saved.UpdatedAtUtc
            }, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, saved);
            return TransportProductionTuningSaveResult.Saved(saved);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Validate(TransportProductionTuningOptions options)
    {
        if (options.AgingPointsPerMinute is < 0 or > 1000)
            return "每分钟老化加分必须在 0 到 1000 之间";
        if (options.MaximumAgingPoints is < 0 or > 10000)
            return "最大老化加分必须在 0 到 10000 之间";
        if (options.DeadlineUrgencyWindowSeconds <= 0)
            return "交期紧迫窗口必须大于 0";
        if (options.MaximumDispatchPerCycle is < 1 or > 100)
            return "每周期最大派单数必须在 1 到 100 之间";
        if (options.SingleTrackOppositeDirectionAgingSeconds <= 0)
            return "单轨反向等待老化时间必须大于 0";
        if (options.TrendRetentionPoints is < 60 or > 100000)
            return "趋势保留点数必须在 60 到 100000 之间";
        if (options.TrendCaptureIntervalSeconds is < 5 or > 3600)
            return "趋势采集间隔必须在 5 到 3600 秒之间";
        if (options.FaultTakeoverCooldownSeconds is < 1 or > 3600)
            return "故障接管冷却时间必须在 1 到 3600 秒之间";
        return null;
    }
}

public interface ITransportStationCongestionService
{
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveDefinitionAsync(TransportStationDefinition definition, CancellationToken cancellationToken = default);
    Task<bool> RemoveDefinitionAsync(string stationId, CancellationToken cancellationToken = default);
    void UpdateOccupancy(string stationId, int occupiedCount);
    void SetQueuedTaskCount(string stationId, int queuedTaskCount);
    TransportStationAdmissionResult Evaluate(string? stationId);
    IReadOnlyList<TransportStationRuntimeSnapshot> GetAll();
}

public sealed class TransportStationCongestionService : ITransportStationCongestionService
{
    private readonly ITransportJournalStore _journal;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ConcurrentDictionary<string, TransportStationDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _occupancy = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _queued = new(StringComparer.Ordinal);

    public TransportStationCongestionService(
        ITransportJournalStore journal,
        ITransportProductionTuningService tuning)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ProductionStation,
            1000,
            cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            var definition = JsonSerializer.Deserialize<TransportStationDefinition>(record.PayloadJson);
            if (definition is not null && !string.IsNullOrWhiteSpace(definition.StationId))
                _definitions[definition.StationId] = definition;
        }
    }

    public async Task SaveDefinitionAsync(
        TransportStationDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.StationId))
            throw new ArgumentException("StationId 不能为空", nameof(definition));
        if (definition.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(definition), "站点容量必须大于 0");
        if (definition.MaximumQueuedTasks < 0)
            throw new ArgumentOutOfRangeException(nameof(definition), "最大排队数不能小于 0");

        _definitions[definition.StationId] = definition;
        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ProductionStation,
            RecordId = definition.StationId,
            PayloadJson = JsonSerializer.Serialize(definition)
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveDefinitionAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryRemove(stationId, out _))
            return false;

        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ProductionStation,
            RecordId = stationId,
            PayloadJson = JsonSerializer.Serialize(new TransportStationDefinition
            {
                StationId = stationId,
                Enabled = false
            })
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void UpdateOccupancy(string stationId, int occupiedCount)
    {
        if (string.IsNullOrWhiteSpace(stationId))
            throw new ArgumentException("StationId 不能为空", nameof(stationId));
        _occupancy[stationId] = Math.Max(0, occupiedCount);
    }

    public void SetQueuedTaskCount(string stationId, int queuedTaskCount)
    {
        if (string.IsNullOrWhiteSpace(stationId))
            return;
        _queued[stationId] = Math.Max(0, queuedTaskCount);
    }

    public TransportStationAdmissionResult Evaluate(string? stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId) || !_definitions.TryGetValue(stationId, out var definition))
            return new TransportStationAdmissionResult { Allowed = true };
        if (!definition.Enabled)
            return new TransportStationAdmissionResult { Reason = $"站点 {stationId} 已停用" };

        var occupied = _occupancy.GetValueOrDefault(stationId);
        var queued = _queued.GetValueOrDefault(stationId);
        var options = _tuning.Current;
        var full = occupied >= definition.Capacity;
        var queueFull = queued >= definition.MaximumQueuedTasks && definition.MaximumQueuedTasks > 0;
        var utilizationPenalty = definition.Capacity == 0
            ? options.FullStationPenalty
            : (int)Math.Round(Math.Min(1d, occupied / (double)definition.Capacity) * options.FullStationPenalty);
        var penalty = utilizationPenalty + queued * options.CongestionPenaltyPerQueuedTask;

        return new TransportStationAdmissionResult
        {
            Allowed = !full && !queueFull,
            CongestionPenalty = penalty,
            Reason = full
                ? $"站点 {stationId} 已满（{occupied}/{definition.Capacity}）"
                : queueFull
                    ? $"站点 {stationId} 排队达到上限（{queued}/{definition.MaximumQueuedTasks}）"
                    : null
        };
    }

    public IReadOnlyList<TransportStationRuntimeSnapshot> GetAll() =>
        _definitions.Values
            .OrderBy(x => x.StationId, StringComparer.Ordinal)
            .Select(definition =>
            {
                var occupied = _occupancy.GetValueOrDefault(definition.StationId);
                var queued = _queued.GetValueOrDefault(definition.StationId);
                return new TransportStationRuntimeSnapshot
                {
                    StationId = definition.StationId,
                    Name = definition.Name,
                    Capacity = definition.Capacity,
                    OccupiedCount = occupied,
                    QueuedTaskCount = queued,
                    MaximumQueuedTasks = definition.MaximumQueuedTasks,
                    Enabled = definition.Enabled,
                    UtilizationPercent = definition.Capacity <= 0
                        ? 0
                        : Math.Round(occupied * 100d / definition.Capacity, 2)
                };
            })
            .ToArray();
}

public interface ITransportSingleTrackCoordinator
{
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveDefinitionAsync(TransportSingleTrackSectionDefinition definition, CancellationToken cancellationToken = default);
    TransportSingleTrackAdmissionResult Evaluate(
        string ownerId,
        string vehicleId,
        int priority,
        IReadOnlyList<string> nodePath,
        DateTime? nowUtc = null);
    void Commit(string ownerId, string vehicleId);
    bool Release(string ownerId, bool requirePhysicalClearance = true);
    void CancelRequest(string ownerId);
    IReadOnlyList<TransportSingleTrackSectionSnapshot> GetSnapshots();
}

public sealed class TransportSingleTrackCoordinator : ITransportSingleTrackCoordinator
{
    private readonly object _sync = new();
    private readonly ITransportJournalStore _journal;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly Dictionary<string, TransportSingleTrackSectionDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TransportSingleTrackPermit>> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TransportSingleTrackWaitingRequest>> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransportSingleTrackAdmissionResult> _pending = new(StringComparer.Ordinal);

    public TransportSingleTrackCoordinator(
        ITransportJournalStore journal,
        ITransportProductionTuningService tuning,
        ITransportTrafficCoordinator traffic)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.SingleTrackSection,
            1000,
            cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var record in records)
            {
                var definition = JsonSerializer.Deserialize<TransportSingleTrackSectionDefinition>(record.PayloadJson);
                if (definition is not null && !string.IsNullOrWhiteSpace(definition.SectionId))
                    SetDefinitionUnsafe(definition);
            }
        }
    }

    public async Task SaveDefinitionAsync(
        TransportSingleTrackSectionDefinition definition,
        CancellationToken cancellationToken = default)
    {
        Validate(definition);
        lock (_sync)
            SetDefinitionUnsafe(definition);

        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.SingleTrackSection,
            RecordId = definition.SectionId,
            PayloadJson = JsonSerializer.Serialize(definition)
        }, cancellationToken).ConfigureAwait(false);
    }

    public TransportSingleTrackAdmissionResult Evaluate(
        string ownerId,
        string vehicleId,
        int priority,
        IReadOnlyList<string> nodePath,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(nodePath);
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_sync)
        {
            var crossing = FindCrossingUnsafe(nodePath);
            if (crossing is null)
                return TransportSingleTrackAdmissionResult.NotRequired();

            var (definition, direction) = crossing.Value;
            var active = _active[definition.SectionId];
            var waiting = _waiting[definition.SectionId];
            var existingWait = waiting.FirstOrDefault(x => string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal));
            if (existingWait is null)
            {
                waiting.Add(new TransportSingleTrackWaitingRequest
                {
                    OwnerId = ownerId,
                    VehicleId = vehicleId,
                    SectionId = definition.SectionId,
                    Direction = direction,
                    Priority = priority,
                    WaitingSinceUtc = now
                });
            }

            var activeDirection = active.FirstOrDefault()?.Direction ?? TransportSingleTrackDirection.None;
            var capacity = Math.Min(definition.Capacity, definition.MaximumSameDirectionConvoy);
            var allowed = active.Count == 0
                ? IsQueueHeadUnsafe(ownerId, definition.SectionId, now)
                : activeDirection == direction &&
                  active.Count < capacity &&
                  !HasAgedOppositeWaitUnsafe(definition.SectionId, direction, now);

            var result = new TransportSingleTrackAdmissionResult
            {
                Required = true,
                Allowed = allowed,
                SectionId = definition.SectionId,
                Direction = direction,
                Reason = allowed
                    ? null
                    : active.Count > 0 && activeDirection != direction
                        ? $"单轨区段 {definition.SectionId} 正在放行相反方向"
                        : $"单轨区段 {definition.SectionId} 存在优先级更高或等待更久的车辆"
            };
            _pending[PendingKey(ownerId, vehicleId)] = result;
            if (allowed)
                waiting.RemoveAll(x => string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal));
            return result;
        }
    }

    public void Commit(string ownerId, string vehicleId)
    {
        lock (_sync)
        {
            var key = PendingKey(ownerId, vehicleId);
            if (!_pending.TryGetValue(key, out var result) ||
                !result.Required ||
                !result.Allowed ||
                string.IsNullOrWhiteSpace(result.SectionId))
            {
                return;
            }

            var permits = _active[result.SectionId];
            if (permits.All(x => !string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal)))
            {
                permits.Add(new TransportSingleTrackPermit
                {
                    OwnerId = ownerId,
                    VehicleId = vehicleId,
                    SectionId = result.SectionId,
                    Direction = result.Direction
                });
            }
            _pending.Remove(key);
        }
    }

    public bool Release(string ownerId, bool requirePhysicalClearance = true)
    {
        lock (_sync)
        {
            foreach (var definition in _definitions.Values)
            {
                if (requirePhysicalClearance && !string.IsNullOrWhiteSpace(definition.TrafficResourceId))
                {
                    var occupied = _traffic.GetHolds().Any(x =>
                        string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal) &&
                        string.Equals(x.ResourceId, definition.TrafficResourceId, StringComparison.Ordinal) &&
                        x.OccupancyConfirmed);
                    if (occupied)
                        return false;
                }
            }

            foreach (var permits in _active.Values)
                permits.RemoveAll(x => string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal));
            foreach (var waits in _waiting.Values)
                waits.RemoveAll(x => string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal));
            foreach (var key in _pending.Keys.Where(x => x.StartsWith(ownerId + ":", StringComparison.Ordinal)).ToArray())
                _pending.Remove(key);
            return true;
        }
    }

    public void CancelRequest(string ownerId)
    {
        lock (_sync)
        {
            foreach (var waits in _waiting.Values)
                waits.RemoveAll(x => string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal));
            foreach (var key in _pending.Keys.Where(x => x.StartsWith(ownerId + ":", StringComparison.Ordinal)).ToArray())
                _pending.Remove(key);
        }
    }

    public IReadOnlyList<TransportSingleTrackSectionSnapshot> GetSnapshots()
    {
        lock (_sync)
        {
            return _definitions.Values
                .OrderBy(x => x.SectionId, StringComparer.Ordinal)
                .Select(definition =>
                {
                    var permits = _active[definition.SectionId].ToArray();
                    return new TransportSingleTrackSectionSnapshot
                    {
                        Definition = definition,
                        ActiveDirection = permits.FirstOrDefault()?.Direction ?? TransportSingleTrackDirection.None,
                        ActivePermits = permits,
                        WaitingRequests = _waiting[definition.SectionId]
                            .OrderByDescending(x => EffectivePriorityUnsafe(x, DateTime.UtcNow))
                            .ThenBy(x => x.WaitingSinceUtc)
                            .ToArray()
                    };
                })
                .ToArray();
        }
    }

    private (TransportSingleTrackSectionDefinition Definition, TransportSingleTrackDirection Direction)? FindCrossingUnsafe(
        IReadOnlyList<string> nodePath)
    {
        foreach (var definition in _definitions.Values.Where(x => x.Enabled))
        {
            var intersections = nodePath
                .Select((node, pathIndex) => new
                {
                    PathIndex = pathIndex,
                    SectionIndex = IndexOf(definition.OrderedNodeIds, node)
                })
                .Where(x => x.SectionIndex >= 0)
                .ToArray();
            if (intersections.Length < 2)
                continue;

            var direction = intersections[^1].SectionIndex > intersections[0].SectionIndex
                ? TransportSingleTrackDirection.Forward
                : TransportSingleTrackDirection.Reverse;
            return (definition, direction);
        }
        return null;
    }

    private bool IsQueueHeadUnsafe(string ownerId, string sectionId, DateTime now)
    {
        var ordered = _waiting[sectionId]
            .OrderByDescending(x => EffectivePriorityUnsafe(x, now))
            .ThenBy(x => x.WaitingSinceUtc)
            .ThenBy(x => x.OwnerId, StringComparer.Ordinal)
            .ToArray();
        return ordered.Length == 0 || string.Equals(ordered[0].OwnerId, ownerId, StringComparison.Ordinal);
    }

    private bool HasAgedOppositeWaitUnsafe(
        string sectionId,
        TransportSingleTrackDirection activeDirection,
        DateTime now) =>
        _waiting[sectionId].Any(x =>
            x.Direction != activeDirection &&
            (now - x.WaitingSinceUtc).TotalSeconds >= _tuning.Current.SingleTrackOppositeDirectionAgingSeconds);

    private int EffectivePriorityUnsafe(TransportSingleTrackWaitingRequest request, DateTime now)
    {
        var ageSeconds = Math.Max(0, (now - request.WaitingSinceUtc).TotalSeconds);
        var aging = (int)(ageSeconds / _tuning.Current.SingleTrackOppositeDirectionAgingSeconds);
        return checked(request.Priority + aging);
    }

    private void SetDefinitionUnsafe(TransportSingleTrackSectionDefinition definition)
    {
        _definitions[definition.SectionId] = definition;
        _active.TryAdd(definition.SectionId, new List<TransportSingleTrackPermit>());
        _waiting.TryAdd(definition.SectionId, new List<TransportSingleTrackWaitingRequest>());
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static void Validate(TransportSingleTrackSectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.SectionId))
            throw new ArgumentException("SectionId 不能为空", nameof(definition));
        if (definition.OrderedNodeIds.Count < 2)
            throw new ArgumentException("单轨区段至少需要两个有序节点", nameof(definition));
        if (definition.OrderedNodeIds.Distinct(StringComparer.Ordinal).Count() != definition.OrderedNodeIds.Count)
            throw new ArgumentException("单轨区段节点不能重复", nameof(definition));
        if (definition.Capacity <= 0 || definition.MaximumSameDirectionConvoy <= 0)
            throw new ArgumentOutOfRangeException(nameof(definition), "单轨容量和同向编队上限必须大于 0");
    }

    private static string PendingKey(string ownerId, string vehicleId) => $"{ownerId}:{vehicleId}";
}

public sealed class TransportSingleTrackDispatchAdmissionPolicy : ITransportDispatchAdmissionPolicy
{
    private readonly ITransportSingleTrackCoordinator _singleTrack;

    public TransportSingleTrackDispatchAdmissionPolicy(ITransportSingleTrackCoordinator singleTrack)
    {
        _singleTrack = singleTrack ?? throw new ArgumentNullException(nameof(singleTrack));
    }

    public TransportDispatchAdmissionResult Evaluate(TransportDispatchAdmissionContext context)
    {
        var fullPath = context.PickupRoute.NodePath
            .Concat(context.LoadedRoute.NodePath.Skip(context.PickupRoute.NodePath.Count > 0 ? 1 : 0))
            .ToArray();
        var result = _singleTrack.Evaluate(
            context.Request.RequestId,
            context.Vehicle.VehicleId,
            context.Request.Priority,
            fullPath,
            context.EvaluatedAtUtc);
        return result.Allowed
            ? TransportDispatchAdmissionResult.Granted()
            : TransportDispatchAdmissionResult.Denied(result.Reason ?? "单轨会车门禁拒绝");
    }

    public void OnAssigned(TransportDispatchAssignment assignment) =>
        _singleTrack.Commit(assignment.RequestId, assignment.VehicleId);

    public void OnCompleted(TransportDispatchAssignment assignment) =>
        _singleTrack.Release(assignment.RequestId);

    public void CancelRequest(string requestId) =>
        _singleTrack.CancelRequest(requestId);
}
