namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// 决策记录在内存中保留最近 5000 条，同时写入既有 TransportJournal，
/// Host 重启后可恢复最近决策用于现场追溯。
/// </summary>
public sealed class JournalTransportDispatchDecisionStore : ITransportDispatchDecisionStore
{
    private const int Capacity = 5000;
    private readonly ITransportJournalStore _journal;
    private readonly ConcurrentQueue<TransportDispatchDecisionFrame> _frames = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _loaded;

    public JournalTransportDispatchDecisionStore(ITransportJournalStore journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _loaded) == 1)
            return;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded == 1)
                return;

            var records = await _journal.QueryAsync(
                TransportJournalCategory.DispatchDecision,
                Capacity,
                cancellationToken).ConfigureAwait(false);
            foreach (var frame in records
                         .Select(x => JsonSerializer.Deserialize<TransportDispatchDecisionFrame>(x.PayloadJson))
                         .Where(x => x is not null)
                         .Cast<TransportDispatchDecisionFrame>()
                         .OrderBy(x => x.OccurredAtUtc))
            {
                Enqueue(frame);
            }

            Volatile.Write(ref _loaded, 1);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void Append(TransportDispatchDecisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Enqueue(frame);
        try
        {
            _journal.UpsertAsync(new TransportJournalRecord
            {
                Category = TransportJournalCategory.DispatchDecision,
                RecordId = frame.DecisionId,
                PayloadJson = JsonSerializer.Serialize(frame),
                OccurredAtUtc = frame.OccurredAtUtc
            }).GetAwaiter().GetResult();
        }
        catch
        {
            // 决策日志落库异常不能反向中断已经完成的现场派单；内存记录仍可用于当前进程追溯。
        }
    }

    public IReadOnlyList<TransportDispatchDecisionFrame> GetRecent(int maxCount = 500) =>
        _frames
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(Math.Clamp(maxCount, 1, Capacity))
            .ToArray();

    private void Enqueue(TransportDispatchDecisionFrame frame)
    {
        _frames.Enqueue(frame);
        while (_frames.Count > Capacity && _frames.TryDequeue(out _))
        {
        }
    }
}

/// <summary>
/// 第九阶段生产派单实现。每一次真实派单周期只累计一次 AttemptCount；
/// 只有执行引擎创建并成功启动后，生产队列才进入 Assigned。
/// </summary>
public sealed class ReliableTransportProductionDispatchService : ITransportProductionDispatchService
{
    private readonly IUnifiedTransportDispatchEngine _dispatch;
    private readonly ITransportExecutionEngine _execution;
    private readonly ITransportDynamicPriorityService _priority;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportDispatchDecisionStore _decisions;
    private readonly ConcurrentDictionary<string, TransportProductionQueueItem> _queue = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    public ReliableTransportProductionDispatchService(
        IUnifiedTransportDispatchEngine dispatch,
        ITransportExecutionEngine execution,
        ITransportDynamicPriorityService priority,
        ITransportStationCongestionService stations,
        ITransportProductionTuningService tuning,
        ITransportDispatchDecisionStore decisions)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _priority = priority ?? throw new ArgumentNullException(nameof(priority));
        _stations = stations ?? throw new ArgumentNullException(nameof(stations));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
    }

    public TransportProductionQueueItem Enqueue(TransportProductionDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var item = new TransportProductionQueueItem
        {
            ProductionRequest = request,
            EffectivePriority = _priority.Calculate(request, DateTime.UtcNow)
        };
        var actual = _queue.GetOrAdd(request.Request.RequestId, item);
        RefreshStationQueues();
        return actual;
    }

    public bool Cancel(string requestId)
    {
        if (!_queue.TryGetValue(requestId, out var current) ||
            current.State == TransportProductionQueueState.Assigned)
            return false;

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
            return false;
        if (!_execution.TryGet(requestId, out var execution) ||
            execution is null ||
            !execution.IsTerminal)
            return false;

        _queue.TryRemove(requestId, out _);
        RefreshStationQueues();
        return true;
    }

    public async Task<TransportProductionDispatchCycleResult> DispatchCycleAsync(
        CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupTerminalAssignments();
            var now = DateTime.UtcNow;
            RefreshPriorities(now);
            var candidates = _queue.Values
                .Where(IsDispatchable)
                .OrderByDescending(x => x.EffectivePriority)
                .ThenBy(x => x.ProductionRequest.EnqueuedAtUtc)
                .ThenBy(x => x.ProductionRequest.Request.RequestId, StringComparer.Ordinal)
                .Take(_tuning.Current.MaximumDispatchPerCycle)
                .ToArray();
            var results = new List<TransportProductionQueueItem>();
            var assignedCount = 0;

            foreach (var current in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestId = current.ProductionRequest.Request.RequestId;
                var station = _stations.Evaluate(current.ProductionRequest.DestinationStationId);
                if (!station.Allowed)
                {
                    var waiting = Save(current with
                    {
                        State = TransportProductionQueueState.WaitingForStation,
                        AttemptCount = current.AttemptCount + 1,
                        LastReason = station.Reason,
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                    results.Add(waiting);
                    RecordDecision(waiting, candidates, null);
                    continue;
                }

                _queue[requestId] = current with
                {
                    State = TransportProductionQueueState.Dispatching,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                var effectiveRequest = CopyWithPriority(
                    current.ProductionRequest.Request,
                    current.EffectivePriority);
                var dispatchResult = await _dispatch.DispatchAsync(
                    effectiveRequest,
                    cancellationToken).ConfigureAwait(false);

                TransportProductionQueueItem final;
                if (dispatchResult.Success && dispatchResult.Assignment is not null)
                {
                    final = StartExecutionOrFail(current, dispatchResult.Assignment);
                    if (final.State == TransportProductionQueueState.Assigned)
                        assignedCount++;
                }
                else
                {
                    final = Save(current with
                    {
                        State = ClassifyFailure(dispatchResult.FailureReason),
                        AttemptCount = current.AttemptCount + 1,
                        LastReason = dispatchResult.FailureReason,
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                }

                results.Add(final);
                RecordDecision(final, candidates, final.AssignedVehicleId);
            }

            RefreshStationQueues();
            return new TransportProductionDispatchCycleResult
            {
                ConsideredCount = candidates.Length,
                AssignedCount = assignedCount,
                WaitingCount = results.Count(x => x.State != TransportProductionQueueState.Assigned),
                Items = results
            };
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public IReadOnlyList<TransportProductionQueueItem> GetQueue()
    {
        CleanupTerminalAssignments();
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
            .Select(x => x with
            {
                EffectivePriority = _priority.Calculate(x.ProductionRequest, now)
            })
            .OrderByDescending(x => x.EffectivePriority)
            .ThenBy(x => x.ProductionRequest.EnqueuedAtUtc)
            .ThenBy(x => x.ProductionRequest.Request.RequestId, StringComparer.Ordinal)
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

    private TransportProductionQueueItem StartExecutionOrFail(
        TransportProductionQueueItem current,
        TransportDispatchAssignment assignment)
    {
        var created = _execution.Create(assignment.RequestId);
        if (!created.Success)
        {
            _dispatch.Complete(assignment.RequestId);
            return Save(current with
            {
                State = TransportProductionQueueState.Failed,
                AttemptCount = current.AttemptCount + 1,
                LastReason = created.FailureReason ?? "执行任务创建失败",
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        var started = _execution.Start(assignment.RequestId);
        if (!started.Success)
        {
            _execution.Cancel(
                assignment.RequestId,
                started.FailureReason ?? "生产派单执行启动失败");
            _dispatch.Complete(assignment.RequestId);
            return Save(current with
            {
                State = TransportProductionQueueState.Failed,
                AttemptCount = current.AttemptCount + 1,
                LastReason = started.FailureReason ?? "执行任务启动失败",
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        return Save(current with
        {
            State = TransportProductionQueueState.Assigned,
            AssignedVehicleId = assignment.VehicleId,
            AttemptCount = current.AttemptCount + 1,
            LastReason = null,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    private void CleanupTerminalAssignments()
    {
        foreach (var pair in _queue)
        {
            if (pair.Value.State != TransportProductionQueueState.Assigned)
                continue;
            if (_execution.TryGet(pair.Key, out var execution) &&
                execution is { IsTerminal: true })
            {
                _queue.TryRemove(pair.Key, out _);
            }
        }
    }

    private TransportProductionQueueItem Save(TransportProductionQueueItem item)
    {
        _queue[item.ProductionRequest.Request.RequestId] = item;
        return item;
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
            TransportProductionQueueState.WaitingForVehicle;

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
