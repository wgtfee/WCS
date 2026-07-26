namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

/// <summary>
/// 对运输执行快照进行旁路周期分析。服务不修改执行状态，只记录阶段轨迹，
/// 并使用已完成正常周期的 Median/MAD 基线识别慢阶段和慢周期。
/// </summary>
public sealed class TransportCycleAnalysisService : ITransportCycleAnalysisService
{
    private const double MadScale = 1.4826;
    private readonly TransportCycleAnalysisOptions _options;
    private readonly ConcurrentDictionary<string, CycleTracker> _trackers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BaselineContext> _baselines = new(StringComparer.Ordinal);
    private readonly object _resultGate = new();
    private readonly Queue<TransportCycleRecord> _cycles = new();
    private readonly Queue<TransportCycleAnomalyRecord> _anomalies = new();
    private long _observedTransitions;
    private long _successfulCycles;
    private long _interruptedCycles;
    private long _invalidSequenceAnomalies;
    private long _durationAnomalies;
    private long _droppedExecutions;

    public TransportCycleAnalysisService(TransportCycleAnalysisOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void Observe(
        TransportExecutionSnapshot? before,
        TransportExecutionSnapshot after,
        string operation,
        bool operationSucceeded)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (!_options.Enabled || string.IsNullOrWhiteSpace(after.RequestId)) return;

        var existed = _trackers.TryGetValue(after.RequestId, out var tracker);
        if (!existed && _trackers.Count >= _options.MaximumTrackedExecutions)
        {
            Interlocked.Increment(ref _droppedExecutions);
            return;
        }

        tracker ??= _trackers.GetOrAdd(after.RequestId, _ => CreateTracker(after));
        TransportCycleRecord? completed = null;
        TransportCycleAnomalyRecord? sequenceAnomaly = null;

        lock (tracker.Gate)
        {
            // 幂等读取、重复 Create 或同状态位置反馈只更新时间，不切分阶段。
            if (tracker.CurrentState == after.State)
            {
                tracker.LastObservedAtUtc = MaxUtc(tracker.LastObservedAtUtc, after.UpdatedAtUtc);
                return;
            }

            Interlocked.Increment(ref _observedTransitions);
            var transitionAt = MaxUtc(tracker.StateEnteredAtUtc, after.UpdatedAtUtc);
            if (!IsAllowedTransition(tracker.CurrentState, after.State))
            {
                tracker.IsSequenceValid = false;
                sequenceAnomaly = new TransportCycleAnomalyRecord
                {
                    AnomalyId = Guid.NewGuid().ToString("N"),
                    RequestId = after.RequestId,
                    VehicleId = after.VehicleId,
                    ContextKey = tracker.ContextKey,
                    Kind = TransportCycleAnomalyKind.InvalidSequence,
                    Phase = tracker.CurrentState,
                    DetectedAtUtc = transitionAt,
                    ActualMilliseconds = 0,
                    Reason = $"运输执行状态顺序异常：{tracker.CurrentState} → {after.State}，操作={operation}，操作成功={operationSucceeded}。"
                };
                Interlocked.Increment(ref _invalidSequenceAnomalies);
            }

            CloseCurrentPhase(tracker, transitionAt);
            tracker.CurrentState = after.State;
            tracker.StateEnteredAtUtc = transitionAt;
            tracker.LastObservedAtUtc = transitionAt;
            tracker.LastError = after.LastError;

            if (IsCycleTerminal(after.State))
                completed = CompleteCycle(tracker, after, transitionAt);
        }

        if (sequenceAnomaly is not null) AddAnomaly(sequenceAnomaly);
        if (completed is null) return;

        _trackers.TryRemove(after.RequestId, out _);
        AddCycle(completed);
        if (completed.IsSuccessful)
        {
            AnalyzeDuration(completed);
            AddToBaseline(completed);
            Interlocked.Increment(ref _successfulCycles);
        }
        else
        {
            Interlocked.Increment(ref _interruptedCycles);
        }
    }

    public IReadOnlyList<TransportCycleRecord> GetCycles(int maximumCount = 200)
    {
        maximumCount = Math.Clamp(maximumCount, 1, Math.Max(1, _options.MaximumCompletedCycles));
        lock (_resultGate)
            return _cycles.Reverse().Take(maximumCount).ToArray();
    }

    public IReadOnlyList<TransportCycleAnomalyRecord> GetAnomalies(int maximumCount = 200)
    {
        maximumCount = Math.Clamp(maximumCount, 1, Math.Max(1, _options.MaximumAnomalies));
        lock (_resultGate)
            return _anomalies.Reverse().Take(maximumCount).ToArray();
    }

    public TransportCycleAnalysisStatus GetStatus()
    {
        int cycleCount;
        lock (_resultGate) cycleCount = _cycles.Count;
        return new TransportCycleAnalysisStatus
        {
            Enabled = _options.Enabled,
            TrackedExecutions = _trackers.Count,
            CompletedCycles = cycleCount,
            BaselineContexts = _baselines.Count,
            ObservedTransitions = Interlocked.Read(ref _observedTransitions),
            SuccessfulCycles = Interlocked.Read(ref _successfulCycles),
            InterruptedCycles = Interlocked.Read(ref _interruptedCycles),
            InvalidSequenceAnomalies = Interlocked.Read(ref _invalidSequenceAnomalies),
            DurationAnomalies = Interlocked.Read(ref _durationAnomalies),
            DroppedExecutions = Interlocked.Read(ref _droppedExecutions)
        };
    }

    private CycleTracker CreateTracker(TransportExecutionSnapshot snapshot)
    {
        var startedAt = snapshot.CreatedAtUtc == default ? snapshot.UpdatedAtUtc : snapshot.CreatedAtUtc;
        if (startedAt == default) startedAt = DateTime.UtcNow;
        return new CycleTracker
        {
            RequestId = snapshot.RequestId,
            VehicleId = snapshot.VehicleId,
            ContextKey = BuildContextKey(snapshot),
            StartedAtUtc = startedAt,
            CurrentState = snapshot.State,
            StateEnteredAtUtc = MaxUtc(startedAt, snapshot.UpdatedAtUtc),
            LastObservedAtUtc = MaxUtc(startedAt, snapshot.UpdatedAtUtc),
            LastError = snapshot.LastError,
            IsSequenceValid = true
        };
    }

    private static void CloseCurrentPhase(CycleTracker tracker, DateTime exitedAtUtc)
    {
        var occurrence = tracker.Occurrences.TryGetValue(tracker.CurrentState, out var current)
            ? current + 1
            : 1;
        tracker.Occurrences[tracker.CurrentState] = occurrence;
        tracker.Phases.Add(new TransportCyclePhaseDuration
        {
            State = tracker.CurrentState,
            EnteredAtUtc = tracker.StateEnteredAtUtc,
            ExitedAtUtc = exitedAtUtc,
            DurationMilliseconds = Math.Max(0, (exitedAtUtc - tracker.StateEnteredAtUtc).TotalMilliseconds),
            Occurrence = occurrence
        });
    }

    private static TransportCycleRecord CompleteCycle(
        CycleTracker tracker,
        TransportExecutionSnapshot snapshot,
        DateTime endedAtUtc) => new()
    {
        CycleId = Guid.NewGuid().ToString("N"),
        RequestId = tracker.RequestId,
        VehicleId = tracker.VehicleId,
        ContextKey = tracker.ContextKey,
        StartedAtUtc = tracker.StartedAtUtc,
        EndedAtUtc = endedAtUtc,
        TerminalState = snapshot.State,
        TotalDurationMilliseconds = Math.Max(0, (endedAtUtc - tracker.StartedAtUtc).TotalMilliseconds),
        Phases = tracker.Phases.ToArray(),
        IsSequenceValid = tracker.IsSequenceValid,
        LastError = snapshot.LastError ?? tracker.LastError
    };

    private void AnalyzeDuration(TransportCycleRecord cycle)
    {
        if (!_baselines.TryGetValue(cycle.ContextKey, out var baseline) ||
            baseline.Count < _options.MinimumBaselineCycles)
            return;

        var totalStats = baseline.GetTotalStatistics(_options.MinimumMadMilliseconds);
        if (cycle.TotalDurationMilliseconds >= _options.MinimumTotalDurationMilliseconds &&
            IsSlowOutlier(cycle.TotalDurationMilliseconds, totalStats, out var totalDeviation))
        {
            AddAnomaly(new TransportCycleAnomalyRecord
            {
                AnomalyId = Guid.NewGuid().ToString("N"),
                RequestId = cycle.RequestId,
                VehicleId = cycle.VehicleId,
                ContextKey = cycle.ContextKey,
                Kind = TransportCycleAnomalyKind.TotalDuration,
                DetectedAtUtc = cycle.EndedAtUtc,
                ActualMilliseconds = cycle.TotalDurationMilliseconds,
                MedianMilliseconds = totalStats.Median,
                ScaledMadMilliseconds = totalStats.ScaledMad,
                Deviation = totalDeviation,
                Reason = $"运输周期耗时异常：实际 {cycle.TotalDurationMilliseconds:F0}ms，中位数 {totalStats.Median:F0}ms，稳健尺度 {totalStats.ScaledMad:F0}ms，偏离 {totalDeviation:F2}。"
            });
            Interlocked.Increment(ref _durationAnomalies);
        }

        foreach (var phase in AggregatePhaseDurations(cycle))
        {
            if (phase.DurationMilliseconds < _options.MinimumPhaseDurationMilliseconds ||
                !baseline.TryGetPhaseStatistics(phase.State, _options.MinimumMadMilliseconds, out var phaseStats) ||
                !IsSlowOutlier(phase.DurationMilliseconds, phaseStats, out var phaseDeviation))
                continue;

            AddAnomaly(new TransportCycleAnomalyRecord
            {
                AnomalyId = Guid.NewGuid().ToString("N"),
                RequestId = cycle.RequestId,
                VehicleId = cycle.VehicleId,
                ContextKey = cycle.ContextKey,
                Kind = TransportCycleAnomalyKind.PhaseDuration,
                Phase = phase.State,
                DetectedAtUtc = cycle.EndedAtUtc,
                ActualMilliseconds = phase.DurationMilliseconds,
                MedianMilliseconds = phaseStats.Median,
                ScaledMadMilliseconds = phaseStats.ScaledMad,
                Deviation = phaseDeviation,
                Reason = $"运输阶段 {phase.State} 耗时异常：实际 {phase.DurationMilliseconds:F0}ms，中位数 {phaseStats.Median:F0}ms，稳健尺度 {phaseStats.ScaledMad:F0}ms，偏离 {phaseDeviation:F2}。"
            });
            Interlocked.Increment(ref _durationAnomalies);
        }
    }

    private bool IsSlowOutlier(double actual, RobustStatistics stats, out double deviation)
    {
        deviation = Math.Max(0, (actual - stats.Median) / Math.Max(stats.ScaledMad, 1e-9));
        return actual > stats.Median && deviation >= _options.MadMultiplier;
    }

    private void AddToBaseline(TransportCycleRecord cycle)
    {
        var baseline = _baselines.GetOrAdd(
            cycle.ContextKey,
            _ => new BaselineContext(_options.MaximumBaselineCyclesPerContext));
        baseline.Add(cycle);
    }

    private void AddCycle(TransportCycleRecord cycle)
    {
        lock (_resultGate)
        {
            _cycles.Enqueue(cycle);
            while (_cycles.Count > _options.MaximumCompletedCycles) _cycles.Dequeue();
        }
    }

    private void AddAnomaly(TransportCycleAnomalyRecord anomaly)
    {
        lock (_resultGate)
        {
            _anomalies.Enqueue(anomaly);
            while (_anomalies.Count > _options.MaximumAnomalies) _anomalies.Dequeue();
        }
    }

    private static IReadOnlyList<PhaseAggregate> AggregatePhaseDurations(TransportCycleRecord cycle) =>
        cycle.Phases
            .GroupBy(static phase => phase.State)
            .Select(group => new PhaseAggregate(group.Key, group.Sum(static phase => phase.DurationMilliseconds)))
            .ToArray();

    private static string BuildContextKey(TransportExecutionSnapshot snapshot)
    {
        var source = snapshot.FullNodePath.FirstOrDefault() ?? snapshot.CurrentNodeId;
        var pickup = snapshot.PickupNodeIndex >= 0 && snapshot.PickupNodeIndex < snapshot.FullNodePath.Count
            ? snapshot.FullNodePath[snapshot.PickupNodeIndex]
            : string.Empty;
        var destination = snapshot.FullNodePath.LastOrDefault() ?? snapshot.CurrentNodeId;
        var loadClass = string.IsNullOrWhiteSpace(snapshot.LoadId) ? "EMPTY" : "LOADED";
        return $"Path={source}>{pickup}>{destination}|Load={loadClass}";
    }

    private static bool IsCycleTerminal(TransportExecutionState state) =>
        state is TransportExecutionState.Completed or TransportExecutionState.Cancelled or TransportExecutionState.Faulted;

    private static bool IsAllowedTransition(TransportExecutionState from, TransportExecutionState to) => from switch
    {
        TransportExecutionState.Assigned => to is
            TransportExecutionState.MovingToPickup or TransportExecutionState.Loading or
            TransportExecutionState.Unloading or TransportExecutionState.Faulted or
            TransportExecutionState.Cancelled,
        TransportExecutionState.MovingToPickup => to is
            TransportExecutionState.Loading or TransportExecutionState.WaitingForRoute or
            TransportExecutionState.Paused or TransportExecutionState.Faulted or
            TransportExecutionState.Cancelled,
        TransportExecutionState.Loading => to is
            TransportExecutionState.MovingToDestination or TransportExecutionState.Paused or
            TransportExecutionState.Faulted or TransportExecutionState.Cancelled,
        TransportExecutionState.MovingToDestination => to is
            TransportExecutionState.Unloading or TransportExecutionState.WaitingForRoute or
            TransportExecutionState.Paused or TransportExecutionState.Faulted or
            TransportExecutionState.Cancelled,
        TransportExecutionState.Unloading => to is
            TransportExecutionState.Completed or TransportExecutionState.Paused or
            TransportExecutionState.Faulted or TransportExecutionState.Cancelled,
        TransportExecutionState.WaitingForRoute => to is
            TransportExecutionState.MovingToPickup or TransportExecutionState.MovingToDestination or
            TransportExecutionState.Loading or TransportExecutionState.Unloading or
            TransportExecutionState.Paused or TransportExecutionState.Faulted or
            TransportExecutionState.Cancelled,
        TransportExecutionState.Paused => to is
            TransportExecutionState.MovingToPickup or TransportExecutionState.MovingToDestination or
            TransportExecutionState.Loading or TransportExecutionState.Unloading or
            TransportExecutionState.WaitingForRoute or TransportExecutionState.Faulted or
            TransportExecutionState.Cancelled,
        _ => false
    };

    private static DateTime MaxUtc(DateTime left, DateTime right)
    {
        if (left == default) return right == default ? DateTime.UtcNow : right;
        if (right == default) return left;
        return left >= right ? left : right;
    }

    private sealed class CycleTracker
    {
        public object Gate { get; } = new();
        public required string RequestId { get; init; }
        public required string VehicleId { get; init; }
        public required string ContextKey { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public required TransportExecutionState CurrentState { get; set; }
        public required DateTime StateEnteredAtUtc { get; set; }
        public required DateTime LastObservedAtUtc { get; set; }
        public required bool IsSequenceValid { get; set; }
        public string? LastError { get; set; }
        public List<TransportCyclePhaseDuration> Phases { get; } = new();
        public Dictionary<TransportExecutionState, int> Occurrences { get; } = new();
    }

    private sealed class BaselineContext
    {
        private readonly int _capacity;
        private readonly object _gate = new();
        private readonly Queue<BaselineCycle> _cycles = new();

        public BaselineContext(int capacity) => _capacity = Math.Max(1, capacity);

        public int Count
        {
            get { lock (_gate) return _cycles.Count; }
        }

        public void Add(TransportCycleRecord cycle)
        {
            var phaseDurations = AggregatePhaseDurations(cycle)
                .ToDictionary(static phase => phase.State, static phase => phase.DurationMilliseconds);
            lock (_gate)
            {
                _cycles.Enqueue(new BaselineCycle(cycle.TotalDurationMilliseconds, phaseDurations));
                while (_cycles.Count > _capacity) _cycles.Dequeue();
            }
        }

        public RobustStatistics GetTotalStatistics(double minimumMad)
        {
            lock (_gate)
                return CalculateStatistics(_cycles.Select(static cycle => cycle.TotalDurationMilliseconds), minimumMad);
        }

        public bool TryGetPhaseStatistics(
            TransportExecutionState state,
            double minimumMad,
            out RobustStatistics statistics)
        {
            lock (_gate)
            {
                var values = _cycles
                    .Where(cycle => cycle.PhaseDurations.ContainsKey(state))
                    .Select(cycle => cycle.PhaseDurations[state])
                    .ToArray();
                if (values.Length == 0)
                {
                    statistics = default;
                    return false;
                }
                statistics = CalculateStatistics(values, minimumMad);
                return true;
            }
        }
    }

    private static RobustStatistics CalculateStatistics(IEnumerable<double> source, double minimumMad)
    {
        var values = source.OrderBy(static value => value).ToArray();
        var median = Median(values);
        var deviations = values.Select(value => Math.Abs(value - median)).OrderBy(static value => value).ToArray();
        return new RobustStatistics(median, Math.Max(Median(deviations) * MadScale, minimumMad));
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var middle = values.Length / 2;
        return values.Length % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2.0;
    }

    private sealed record BaselineCycle(
        double TotalDurationMilliseconds,
        IReadOnlyDictionary<TransportExecutionState, double> PhaseDurations);
    private sealed record PhaseAggregate(TransportExecutionState State, double DurationMilliseconds);
    private readonly record struct RobustStatistics(double Median, double ScaledMad);
}
