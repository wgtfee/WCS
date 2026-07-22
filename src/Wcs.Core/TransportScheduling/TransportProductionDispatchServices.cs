namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using System.Text.Json;

public interface ITransportDynamicPriorityService
{
    int Calculate(TransportProductionDispatchRequest request, DateTime nowUtc);
}

public sealed class TransportDynamicPriorityService : ITransportDynamicPriorityService
{
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportStationCongestionService _stations;

    public TransportDynamicPriorityService(
        ITransportProductionTuningService tuning,
        ITransportStationCongestionService stations)
    {
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _stations = stations ?? throw new ArgumentNullException(nameof(stations));
    }

    public int Calculate(TransportProductionDispatchRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _tuning.Current;
        var waitedMinutes = Math.Max(0, (nowUtc - request.EnqueuedAtUtc).TotalMinutes);
        var aging = Math.Min(
            options.MaximumAgingPoints,
            (int)Math.Floor(waitedMinutes) * options.AgingPointsPerMinute);
        var deadline = 0;
        if (request.DeadlineAtUtc.HasValue)
        {
            var remaining = (request.DeadlineAtUtc.Value - nowUtc).TotalSeconds;
            if (remaining <= 0)
                deadline = options.DeadlineUrgencyPoints * 2;
            else if (remaining <= options.DeadlineUrgencyWindowSeconds)
                deadline = options.DeadlineUrgencyPoints;
        }

        var recovery = request.IsRecoveryTask ? options.RecoveryTaskBoost : 0;
        var congestion = _stations.Evaluate(request.DestinationStationId).CongestionPenalty;
        return checked(
            request.Request.Priority +
            request.ProductionOrderPriority +
            aging +
            deadline +
            recovery -
            congestion);
    }
}

public interface ITransportDispatchDecisionStore
{
    void Append(TransportDispatchDecisionFrame frame);
    IReadOnlyList<TransportDispatchDecisionFrame> GetRecent(int maxCount = 500);
}

public sealed class InMemoryTransportDispatchDecisionStore : ITransportDispatchDecisionStore
{
    private readonly ConcurrentQueue<TransportDispatchDecisionFrame> _frames = new();
    private const int Capacity = 5000;

    public void Append(TransportDispatchDecisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frames.Enqueue(frame);
        while (_frames.Count > Capacity && _frames.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<TransportDispatchDecisionFrame> GetRecent(int maxCount = 500) =>
        _frames
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(Math.Clamp(maxCount, 1, Capacity))
            .ToArray();
}

public interface ITransportProductionDispatchService
{
    TransportProductionQueueItem Enqueue(TransportProductionDispatchRequest request);
    bool Cancel(string requestId);
    bool Complete(string requestId);
    Task<TransportProductionDispatchCycleResult> DispatchCycleAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<TransportProductionQueueItem> GetQueue();
    TransportProductionDryRunReport DryRun(DateTime? nowUtc = null);
    IReadOnlyList<TransportDispatchDecisionFrame> GetDecisions(int maxCount = 500);
}

public sealed class TransportProductionDispatchService : ITransportProductionDispatchService
{
    private readonly IUnifiedTransportDispatchEngine _dispatch;
    private readonly ITransportDynamicPriorityService _priority;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportDispatchDecisionStore _decisions;
    private readonly ConcurrentDictionary<string, TransportProductionQueueItem> _queue = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    public TransportProductionDispatchService(
        IUnifiedTransportDispatchEngine dispatch,
        ITransportDynamicPriorityService priority,
        ITransportStationCongestionService stations,
        ITransportProductionTuningService tuning,
        ITransportDispatchDecisionStore decisions)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _priority = priority ?? throw new ArgumentNullException(nameof(priority));
        _stations = stations ?? throw new ArgumentNullException(nameof(stations));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
    }

    public TransportProductionQueueItem Enqueue(TransportProductionDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var requestId = request.Request.RequestId;
        var item = new TransportProductionQueueItem
        {
            ProductionRequest = request,
            EffectivePriority = _priority.Calculate(request, DateTime.UtcNow)
        };
        var actual = _queue.GetOrAdd(requestId, item);
        RefreshStationQueues();
        return actual;
    }

    public bool Cancel(string requestId)
    {
        if (!_queue.TryGetValue(requestId, out var current) ||
            current.State == TransportProductionQueueState.Assigned)
        {
            return false;
        }

        _queue[requestId] = current with
        {
            State = TransportProductionQueueState.Cancelled,
            UpdatedAtUtc = DateTime.UtcNow
        };
        RefreshStationQueues();
        return true;
    }

    public bool Complete(string requestId)
    {
        if (!_queue.TryGetValue(requestId, out var current) ||
            current.State != TransportProductionQueueState.Assigned)
        {
            return false;
        }

        var completed = _dispatch.Complete(requestId);
        if (completed)
            _queue.TryRemove(requestId, out _);
        RefreshStationQueues();
        return completed;
    }

    public async Task<TransportProductionDispatchCycleResult> DispatchCycleAsync(
        CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            RefreshPriorities(now);
            var candidates = _queue.Values
                .Where(IsDispatchable)
                .OrderByDescending(x => x.EffectivePriority)
                .ThenBy(x => x.ProductionRequest.EnqueuedAtUtc)
                .ThenBy(x => x.ProductionRequest.Request.RequestId, StringComparer.Ordinal)
                .Take(_tuning.Current.MaximumDispatchPerCycle)
                .ToArray();
            var cycleItems = new List<TransportProductionQueueItem>();
            var assignedCount = 0;

            foreach (var current in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestId = current.ProductionRequest.Request.RequestId;
                var stationAdmission = _stations.Evaluate(current.ProductionRequest.DestinationStationId);
                if (!stationAdmission.Allowed)
                {
                    var waiting = Update(current, TransportProductionQueueState.WaitingForStation, stationAdmission.Reason);
                    cycleItems.Add(waiting);
                    RecordDecision(waiting, candidates, null);
                    continue;
                }

                var dispatching = Update(current, TransportProductionQueueState.Dispatching, null);
                var effectiveRequest = CopyWithPriority(
                    dispatching.ProductionRequest.Request,
                    dispatching.EffectivePriority);
                var result = await _dispatch.DispatchAsync(effectiveRequest, cancellationToken).ConfigureAwait(false);
                if (result.Success && result.Assignment is not null)
                {
                    var assigned = dispatching with
                    {
                        State = TransportProductionQueueState.Assigned,
                        AssignedVehicleId = result.Assignment.VehicleId,
                        AttemptCount = dispatching.AttemptCount + 1,
                        LastReason = null,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    _queue[requestId] = assigned;
                    cycleItems.Add(assigned);
                    assignedCount++;
                    RecordDecision(assigned, candidates, result.Assignment.VehicleId);
                    continue;
                }

                var nextState = ClassifyFailure(result.FailureReason);
                var failed = dispatching with
                {
                    State = nextState,
                    AttemptCount = dispatching.AttemptCount + 1,
                    LastReason = result.FailureReason,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                _queue[requestId] = failed;
                cycleItems.Add(failed);
                RecordDecision(failed, candidates, null);
            }

            RefreshStationQueues();
            return new TransportProductionDispatchCycleResult
            {
                ConsideredCount = candidates.Length,
                AssignedCount = assignedCount,
                WaitingCount = cycleItems.Count(x => x.State is not TransportProductionQueueState.Assigned),
                Items = cycleItems
            };
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public IReadOnlyList<TransportProductionQueueItem> GetQueue()
    {
        RefreshPriorities(DateTime.UtcNow);
        return _queue.Values
            .OrderByDescending(x => x.EffectivePriority)
            .ThenBy(x => x.ProductionRequest.EnqueuedAtUtc)
            .ToArray();
    }

    public TransportProductionDryRunReport DryRun(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var ranked = _queue.Values
            .Where(IsDispatchable)
            .Select(item => item with
            {
                EffectivePriority = _priority.Calculate(item.ProductionRequest, now)
            })
            .OrderByDescending(x => x.EffectivePriority)
            .ThenBy(x => x.ProductionRequest.EnqueuedAtUtc)
            .ToArray();

        return new TransportProductionDryRunReport
        {
            Items = ranked.Select((item, index) =>
            {
                var station = _stations.Evaluate(item.ProductionRequest.DestinationStationId);
                return new TransportProductionDryRunItem
                {
                    RequestId = item.ProductionRequest.Request.RequestId,
                    EffectivePriority = item.EffectivePriority,
                    Rank = index + 1,
                    StationAdmitted = station.Allowed,
                    Explanation = station.Allowed
                        ? $"动态优先级 {item.EffectivePriority}，可进入派单竞争"
                        : station.Reason
                };
            }).ToArray()
        };
    }

    public IReadOnlyList<TransportDispatchDecisionFrame> GetDecisions(int maxCount = 500) =>
        _decisions.GetRecent(maxCount);

    private TransportProductionQueueItem Update(
        TransportProductionQueueItem current,
        TransportProductionQueueState state,
        string? reason)
    {
        var updated = current with
        {
            State = state,
            AttemptCount = current.AttemptCount + 1,
            LastReason = reason,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _queue[current.ProductionRequest.Request.RequestId] = updated;
        return updated;
    }

    private void RefreshPriorities(DateTime now)
    {
        foreach (var pair in _queue)
        {
            if (!IsDispatchable(pair.Value))
                continue;
            _queue[pair.Key] = pair.Value with
            {
                EffectivePriority = _priority.Calculate(pair.Value.ProductionRequest, now)
            };
        }
    }

    private void RefreshStationQueues()
    {
        var counts = _queue.Values
            .Where(IsDispatchable)
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductionRequest.DestinationStationId))
            .GroupBy(x => x.ProductionRequest.DestinationStationId!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        foreach (var station in _stations.GetAll())
            _stations.SetQueuedTaskCount(station.StationId, counts.GetValueOrDefault(station.StationId));
    }

    private void RecordDecision(
        TransportProductionQueueItem item,
        IReadOnlyCollection<TransportProductionQueueItem> competitors,
        string? vehicleId)
    {
        _decisions.Append(new TransportDispatchDecisionFrame
        {
            RequestId = item.ProductionRequest.Request.RequestId,
            EffectivePriority = item.EffectivePriority,
            ResultState = item.State,
            VehicleId = vehicleId,
            Reason = item.LastReason,
            CompetingRequestIds = competitors
                .Where(x => !string.Equals(
                    x.ProductionRequest.Request.RequestId,
                    item.ProductionRequest.Request.RequestId,
                    StringComparison.Ordinal))
                .Select(x => x.ProductionRequest.Request.RequestId)
                .ToArray()
        });
    }

    private static bool IsDispatchable(TransportProductionQueueItem item) =>
        item.State is
            TransportProductionQueueState.Queued or
            TransportProductionQueueState.WaitingForStation or
            TransportProductionQueueState.WaitingForTraffic or
            TransportProductionQueueState.WaitingForVehicle or
            TransportProductionQueueState.Failed;

    private static TransportProductionQueueState ClassifyFailure(string? reason)
    {
        if (reason?.Contains("单轨", StringComparison.Ordinal) == true ||
            reason?.Contains("交通", StringComparison.Ordinal) == true ||
            reason?.Contains("预留", StringComparison.Ordinal) == true)
            return TransportProductionQueueState.WaitingForTraffic;
        if (reason?.Contains("车辆", StringComparison.Ordinal) == true ||
            reason?.Contains("电量", StringComparison.Ordinal) == true)
            return TransportProductionQueueState.WaitingForVehicle;
        return TransportProductionQueueState.Failed;
    }

    private static TransportDispatchRequest CopyWithPriority(TransportDispatchRequest request, int priority) => new()
    {
        RequestId = request.RequestId,
        SourceNodeId = request.SourceNodeId,
        DestinationNodeId = request.DestinationNodeId,
        LoadId = request.LoadId,
        Priority = priority,
        RequiredCapability = request.RequiredCapability,
        RequiredEdgeCapability = request.RequiredEdgeCapability,
        AllowedVehicleKinds = request.AllowedVehicleKinds,
        RouteStrategy = request.RouteStrategy,
        ReservationLease = request.ReservationLease,
        ReservationWindowEdges = request.ReservationWindowEdges,
        MinimumBatteryPercent = request.MinimumBatteryPercent,
        AllowLowBatteryOverride = request.AllowLowBatteryOverride,
        RequiredVehicleId = request.RequiredVehicleId
    };

    private static void Validate(TransportProductionDispatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Request.RequestId))
            throw new ArgumentException("RequestId 不能为空", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Request.SourceNodeId) ||
            string.IsNullOrWhiteSpace(request.Request.DestinationNodeId))
            throw new ArgumentException("起点和终点不能为空", nameof(request));
    }
}

public interface ITransportProductionTrendService
{
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task<TransportProductionTrendPoint> CaptureAsync(CancellationToken cancellationToken = default);
    TransportProductionTrendSummary GetSummary(DateTime fromUtc, DateTime toUtc);
}

public sealed class TransportProductionTrendService : ITransportProductionTrendService
{
    private readonly object _sync = new();
    private readonly ITransportProductionDispatchService _production;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportPerformanceService _performance;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportJournalStore _journal;
    private readonly List<TransportProductionTrendPoint> _points = new();

    public TransportProductionTrendService(
        ITransportProductionDispatchService production,
        ITransportStationCongestionService stations,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportVehicleRegistry vehicles,
        ITransportPerformanceService performance,
        ITransportProductionTuningService tuning,
        ITransportJournalStore journal)
    {
        _production = production;
        _stations = stations;
        _singleTrack = singleTrack;
        _vehicles = vehicles;
        _performance = performance;
        _tuning = tuning;
        _journal = journal;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ProductionTrend,
            _tuning.Current.TrendRetentionPoints,
            cancellationToken).ConfigureAwait(false);
        var restored = records
            .Select(x => JsonSerializer.Deserialize<TransportProductionTrendPoint>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportProductionTrendPoint>()
            .OrderBy(x => x.CapturedAtUtc)
            .ToArray();
        lock (_sync)
        {
            _points.Clear();
            _points.AddRange(restored);
            TrimUnsafe();
        }
    }

    public async Task<TransportProductionTrendPoint> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var queue = _production.GetQueue();
        var stations = _stations.GetAll();
        var tracks = _singleTrack.GetSnapshots();
        var vehicles = _vehicles.GetAll();
        var performance = _performance.GetSnapshot();
        var point = new TransportProductionTrendPoint
        {
            QueueLength = queue.Count(x => x.State is not TransportProductionQueueState.Assigned),
            WaitingForStationCount = queue.Count(x => x.State == TransportProductionQueueState.WaitingForStation),
            WaitingForTrafficCount = queue.Count(x => x.State == TransportProductionQueueState.WaitingForTraffic),
            FaultedVehicleCount = vehicles.Count(x =>
                !x.IsOnline || x.State == TransportVehicleOperatingState.Faulted),
            SingleTrackWaitingCount = tracks.Sum(x => x.WaitingRequests.Count),
            MaximumStationUtilizationPercent = stations.Count == 0
                ? 0
                : stations.Max(x => x.UtilizationPercent),
            FleetUtilizationPercent = performance.FleetUtilizationPercent,
            CompletionRatePercent = performance.CompletionRatePercent
        };

        lock (_sync)
        {
            _points.Add(point);
            TrimUnsafe();
        }
        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ProductionTrend,
            RecordId = point.CapturedAtUtc.ToString("yyyyMMddHHmmss"),
            PayloadJson = JsonSerializer.Serialize(point),
            OccurredAtUtc = point.CapturedAtUtc
        }, cancellationToken).ConfigureAwait(false);
        return point;
    }

    public TransportProductionTrendSummary GetSummary(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc < fromUtc)
            throw new ArgumentException("ToUtc 不能早于 FromUtc");
        lock (_sync)
        {
            var selected = _points
                .Where(x => x.CapturedAtUtc >= fromUtc && x.CapturedAtUtc <= toUtc)
                .OrderBy(x => x.CapturedAtUtc)
                .ToArray();
            return new TransportProductionTrendSummary
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                PointCount = selected.Length,
                AverageQueueLength = selected.Length == 0 ? 0 : Math.Round(selected.Average(x => x.QueueLength), 2),
                MaximumQueueLength = selected.Length == 0 ? 0 : selected.Max(x => x.QueueLength),
                AverageFleetUtilizationPercent = selected.Length == 0 ? 0 : Math.Round(selected.Average(x => x.FleetUtilizationPercent), 2),
                AverageCompletionRatePercent = selected.Length == 0 ? 0 : Math.Round(selected.Average(x => x.CompletionRatePercent), 2),
                MaximumStationUtilizationPercent = selected.Length == 0 ? 0 : selected.Max(x => x.MaximumStationUtilizationPercent),
                Points = selected
            };
        }
    }

    private void TrimUnsafe()
    {
        var excess = _points.Count - _tuning.Current.TrendRetentionPoints;
        if (excess > 0)
            _points.RemoveRange(0, excess);
    }
}

public interface ITransportFaultTakeoverService
{
    Task<TransportFaultTakeoverReport> EvaluateAsync(CancellationToken cancellationToken = default);
    TransportFaultTakeoverReport GetLastReport();
}

public sealed class TransportFaultTakeoverService : ITransportFaultTakeoverService
{
    private readonly ITransportExecutionEngine _executions;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportTaskReassignmentService _reassignments;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ConcurrentDictionary<string, DateTime> _lastAttempts = new(StringComparer.Ordinal);
    private TransportFaultTakeoverReport _lastReport = new();

    public TransportFaultTakeoverService(
        ITransportExecutionEngine executions,
        ITransportVehicleRegistry vehicles,
        ITransportTaskReassignmentService reassignments,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportProductionTuningService tuning)
    {
        _executions = executions;
        _vehicles = vehicles;
        _reassignments = reassignments;
        _singleTrack = singleTrack;
        _tuning = tuning;
    }

    public async Task<TransportFaultTakeoverReport> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var items = new List<TransportFaultTakeoverItem>();
        foreach (var execution in _executions.GetAll().Where(x => !x.IsTerminal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _vehicles.TryGet(execution.VehicleId, out var vehicle);
            if (vehicle is { IsOnline: true } && vehicle.State != TransportVehicleOperatingState.Faulted)
                continue;
            if (_lastAttempts.TryGetValue(execution.RequestId, out var last) &&
                (now - last).TotalSeconds < _tuning.Current.FaultTakeoverCooldownSeconds)
            {
                items.Add(Item(execution, TransportFaultTakeoverDecision.Skipped, "仍处于故障接管冷却窗口"));
                continue;
            }
            _lastAttempts[execution.RequestId] = now;

            try
            {
                var result = await _reassignments.ReassignAsync(
                    execution.RequestId,
                    "第九阶段故障车辆自动接管",
                    true,
                    cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    var released = _singleTrack.Release(execution.RequestId, requirePhysicalClearance: true);
                    items.Add(new TransportFaultTakeoverItem
                    {
                        RequestId = execution.RequestId,
                        VehicleId = execution.VehicleId,
                        ReplacementVehicleId = result.Record.ReplacementVehicleId,
                        Decision = released
                            ? TransportFaultTakeoverDecision.Reassigned
                            : TransportFaultTakeoverDecision.WaitingForPhysicalClearance,
                        Message = released
                            ? "故障任务已安全转移到接替车辆"
                            : "接替任务已创建，但原车辆仍有已确认物理占用，保留单轨许可等待现场清场"
                    });
                    continue;
                }

                var decision = result.Record.Decision switch
                {
                    TransportReassignmentDecision.ManualRecoveryRequired => TransportFaultTakeoverDecision.ManualRecoveryRequired,
                    TransportReassignmentDecision.NoAlternativeVehicle => TransportFaultTakeoverDecision.NoAlternativeVehicle,
                    TransportReassignmentDecision.SkippedTerminal => TransportFaultTakeoverDecision.Skipped,
                    _ => TransportFaultTakeoverDecision.Failed
                };
                items.Add(Item(execution, decision, result.Record.Reason));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(Item(execution, TransportFaultTakeoverDecision.Failed, ex.Message));
            }
        }

        var report = new TransportFaultTakeoverReport { Items = items };
        Volatile.Write(ref _lastReport, report);
        return report;
    }

    public TransportFaultTakeoverReport GetLastReport() => Volatile.Read(ref _lastReport);

    private static TransportFaultTakeoverItem Item(
        TransportExecutionSnapshot execution,
        TransportFaultTakeoverDecision decision,
        string message) => new()
    {
        RequestId = execution.RequestId,
        VehicleId = execution.VehicleId,
        Decision = decision,
        Message = message
    };
}
