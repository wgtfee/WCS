namespace Wcs.Core.AnomalyDetection;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// PLC 异常检测引擎 v1：阈值、变化率、持续时间与 Median/MAD 动态基线。
/// 检测器只生成候选，连续命中/连续恢复状态机负责抑制毛刺和报警风暴。
/// </summary>
public sealed class PlcAnomalyEngine : IPlcAnomalyEngine, IPlcAnomalyStatusProvider
{
    private const string ModelVersion = "rules-mad-v1";

    private readonly PlcAnomalyOptions _options;
    private readonly IEventBus _eventBus;
    private readonly ConcurrentDictionary<string, SignalRuleState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PlcAnomalyRecord> _active = new(StringComparer.Ordinal);

    private long _processedSamples;
    private long _detectorObservations;
    private long _raised;
    private long _recovered;
    private long _suppressed;
    private long _failures;
    private long _lastProcessedTicks;
    private string? _lastError;

    public PlcAnomalyEngine(PlcAnomalyOptions options, IEventBus eventBus)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    public async ValueTask ProcessAsync(
        PlcAnomalySample sample,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _processedSamples);
        Volatile.Write(ref _lastProcessedTicks, sample.TimestampUtc.Ticks);

        try
        {
            List<AnomalyTransition>? transitions = null;
            foreach (var rule in _options.Rules)
            {
                if (!RuleMatches(rule, sample)) continue;

                var stateKey = BuildStateKey(rule.RuleId, sample);
                if (!_states.TryGetValue(stateKey, out var state))
                {
                    if (_states.Count >= _options.MaximumTrackedRuleSignals)
                    {
                        Interlocked.Increment(ref _suppressed);
                        continue;
                    }

                    state = _states.GetOrAdd(stateKey, _ => new SignalRuleState(rule));
                }

                AnomalyTransition? transition;
                lock (state.Gate)
                {
                    transition = EvaluateSampleLocked(state, sample);
                }

                if (transition is not null)
                {
                    transitions ??= new List<AnomalyTransition>();
                    transitions.Add(transition);
                }
            }

            if (transitions is not null)
            {
                foreach (var transition in transitions)
                    await PublishTransitionAsync(transition, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            Volatile.Write(ref _lastError, ex.Message);
        }
    }

    /// <summary>周期检查保持为 true 的持续时间规则，即使 PLC 没有再次产生边沿也能触发。</summary>
    public async ValueTask SweepAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        List<AnomalyTransition>? transitions = null;
        foreach (var state in _states.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Rule.MaximumTrueDurationMs is null) continue;

            AnomalyTransition? transition = null;
            lock (state.Gate)
            {
                if (state.LastSample?.BooleanValue != true || state.TrueSinceUtc is null)
                    continue;

                var candidate = EvaluateDuration(state.Rule, state.LastSample, state.TrueSinceUtc.Value, utcNow);
                transition = ApplyCandidateLocked(state, state.LastSample with { TimestampUtc = utcNow }, candidate);
            }

            if (transition is not null)
            {
                transitions ??= new List<AnomalyTransition>();
                transitions.Add(transition);
            }
        }

        if (transitions is not null)
        {
            foreach (var transition in transitions)
                await PublishTransitionAsync(transition, cancellationToken);
        }
    }

    public IReadOnlyList<PlcAnomalyRecord> GetActiveAnomalies() =>
        _active.Values.OrderByDescending(static item => item.StartTimeUtc).ToList();

    public PlcAnomalyStatus GetStatus()
    {
        var ticks = Volatile.Read(ref _lastProcessedTicks);
        return new PlcAnomalyStatus
        {
            Enabled = _options.Enabled,
            ProcessedSamples = Interlocked.Read(ref _processedSamples),
            DetectorObservations = Interlocked.Read(ref _detectorObservations),
            Raised = Interlocked.Read(ref _raised),
            Recovered = Interlocked.Read(ref _recovered),
            Suppressed = Interlocked.Read(ref _suppressed),
            Failures = Interlocked.Read(ref _failures),
            TrackedRuleSignals = _states.Count,
            ActiveAnomalies = _active.Count,
            LastProcessedUtc = ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc),
            LastError = Volatile.Read(ref _lastError)
        };
    }

    private AnomalyTransition? EvaluateSampleLocked(SignalRuleState state, PlcAnomalySample sample)
    {
        var candidates = new List<DetectorCandidate>(4);

        if (sample.NumericValue is { } numeric)
        {
            var threshold = EvaluateThreshold(state.Rule, numeric);
            if (threshold is not null) candidates.Add(threshold);

            var rate = EvaluateRate(state.Rule, state, sample, numeric);
            if (rate is not null) candidates.Add(rate);

            var baseline = EvaluateStatisticalBaseline(state.Rule, state, numeric);
            if (baseline is not null) candidates.Add(baseline);
        }

        if (sample.BooleanValue is { } boolean)
        {
            if (boolean)
                state.TrueSinceUtc ??= sample.TimestampUtc;
            else
                state.TrueSinceUtc = null;

            if (boolean && state.TrueSinceUtc is { } trueSince)
            {
                var duration = EvaluateDuration(state.Rule, sample, trueSince, sample.TimestampUtc);
                if (duration is not null) candidates.Add(duration);
            }
        }

        var candidate = candidates.Count == 0
            ? null
            : candidates.OrderByDescending(static item => item.Score).First();

        if (candidate is not null)
            Interlocked.Increment(ref _detectorObservations);

        var transition = ApplyCandidateLocked(state, sample, candidate);

        state.LastSample = sample;
        if (sample.NumericValue is { } current)
        {
            state.LastNumericValue = current;
            state.LastNumericUtc = sample.TimestampUtc;

            // 异常值不回灌动态基线，避免基线被故障值带偏。
            if (candidate is null || candidate.Type != PlcAnomalyType.StatisticalBaseline)
                AddWindowValue(state, current);
        }

        return transition;
    }

    private AnomalyTransition? ApplyCandidateLocked(
        SignalRuleState state,
        PlcAnomalySample sample,
        DetectorCandidate? candidate)
    {
        var abnormal = candidate is not null && candidate.Score >= _options.ObserveThreshold;
        if (abnormal)
        {
            state.NormalCount = 0;
            state.AbnormalCount++;
            state.FirstAbnormalUtc ??= sample.TimestampUtc;

            var required = state.Rule.ConsecutiveAbnormalCount ??
                (candidate!.Score >= _options.AlarmThreshold
                    ? _options.ConsecutiveAlarmCount
                    : _options.ConsecutiveWarningCount);

            if (state.Active is null && state.AbnormalCount >= Math.Max(1, required))
            {
                var record = CreateRecord(state.Rule, sample, candidate!, state.FirstAbnormalUtc.Value);
                state.Active = record;
                _active[record.AnomalyKey] = record;
                Interlocked.Increment(ref _raised);
                return new AnomalyTransition(true, record);
            }

            if (state.Active is not null)
            {
                var updated = state.Active with
                {
                    Type = candidate!.Type,
                    DetectorName = candidate.DetectorName,
                    Severity = ResolveSeverity(state.Rule.Severity, candidate.Score),
                    Score = Math.Max(state.Active.Score, candidate.Score),
                    ActualValue = candidate.ActualValue,
                    ExpectedValue = candidate.ExpectedValue,
                    LowerBound = candidate.LowerBound,
                    UpperBound = candidate.UpperBound,
                    LastSeenUtc = sample.TimestampUtc,
                    Reason = candidate.Reason,
                    ContextJson = CreateContextJson(state.Rule, sample, candidate)
                };
                state.Active = updated;
                _active[updated.AnomalyKey] = updated;
            }

            return null;
        }

        state.AbnormalCount = 0;
        state.FirstAbnormalUtc = null;
        if (state.Active is null)
        {
            state.NormalCount = 0;
            return null;
        }

        state.NormalCount++;
        var recoveryRequired = Math.Max(
            1,
            state.Rule.ConsecutiveRecoveryCount ?? _options.RecoveryCount);
        if (state.NormalCount < recoveryRequired) return null;

        var recovered = state.Active.Recover(sample.TimestampUtc);
        state.Active = null;
        state.NormalCount = 0;
        _active.TryRemove(recovered.AnomalyKey, out _);
        Interlocked.Increment(ref _recovered);
        return new AnomalyTransition(false, recovered);
    }

    private async Task PublishTransitionAsync(
        AnomalyTransition transition,
        CancellationToken cancellationToken)
    {
        if (transition.IsDetected)
        {
            await _eventBus.PublishAsync(new PlcAnomalyDetectedEvent
            {
                Anomaly = transition.Record
            }, cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(new PlcAnomalyRecoveredEvent
            {
                Anomaly = transition.Record
            }, cancellationToken);
        }
    }

    private PlcAnomalyRecord CreateRecord(
        PlcAnomalyRule rule,
        PlcAnomalySample sample,
        DetectorCandidate candidate,
        DateTime startUtc)
    {
        var anomalyKey = BuildStateKey(rule.RuleId, sample);
        return new PlcAnomalyRecord
        {
            AnomalyId = Guid.NewGuid().ToString("N"),
            AnomalyKey = anomalyKey,
            AlarmCode = BuildAlarmCode(anomalyKey),
            RuleId = rule.RuleId,
            Type = candidate.Type,
            Severity = ResolveSeverity(rule.Severity, candidate.Score),
            Status = PlcAnomalyLifecycleStatus.Active,
            PlcName = sample.PlcName,
            DbBlock = sample.DbBlock,
            DeviceId = sample.DeviceId,
            SignalName = sample.SignalName,
            DetectorName = candidate.DetectorName,
            ModelVersion = ModelVersion,
            Score = candidate.Score,
            ActualValue = candidate.ActualValue,
            ExpectedValue = candidate.ExpectedValue,
            LowerBound = candidate.LowerBound,
            UpperBound = candidate.UpperBound,
            StartTimeUtc = startUtc,
            LastSeenUtc = sample.TimestampUtc,
            Reason = candidate.Reason,
            TaskId = sample.TaskId,
            RaiseAlarm = rule.RaiseAlarm,
            ContextJson = CreateContextJson(rule, sample, candidate)
        };
    }

    private static DetectorCandidate? EvaluateThreshold(PlcAnomalyRule rule, double value)
    {
        if (rule.Maximum is { } maximum && value > maximum)
        {
            var ratio = (value - maximum) / Math.Max(Math.Abs(maximum), 1.0);
            return new DetectorCandidate(
                PlcAnomalyType.Threshold,
                "ThresholdDetector",
                ScoreFromExceedance(ratio),
                value,
                maximum,
                rule.Minimum,
                maximum,
                $"当前值 {value:G6} 超过上限 {maximum:G6}");
        }

        if (rule.Minimum is { } minimum && value < minimum)
        {
            var ratio = (minimum - value) / Math.Max(Math.Abs(minimum), 1.0);
            return new DetectorCandidate(
                PlcAnomalyType.Threshold,
                "ThresholdDetector",
                ScoreFromExceedance(ratio),
                value,
                minimum,
                minimum,
                rule.Maximum,
                $"当前值 {value:G6} 低于下限 {minimum:G6}");
        }

        return null;
    }

    private static DetectorCandidate? EvaluateRate(
        PlcAnomalyRule rule,
        SignalRuleState state,
        PlcAnomalySample sample,
        double value)
    {
        if (rule.MaximumRatePerSecond is not { } maximumRate ||
            state.LastNumericValue is not { } previous ||
            state.LastNumericUtc is not { } previousUtc)
            return null;

        var elapsed = (sample.TimestampUtc - previousUtc).TotalSeconds;
        if (elapsed <= 0) return null;

        var rate = Math.Abs(value - previous) / elapsed;
        if (rate <= maximumRate) return null;

        var ratio = rate / Math.Max(maximumRate, double.Epsilon) - 1.0;
        return new DetectorCandidate(
            PlcAnomalyType.RateOfChange,
            "RateOfChangeDetector",
            ScoreFromExceedance(ratio),
            rate,
            maximumRate,
            0,
            maximumRate,
            $"变化率 {rate:G6}/s 超过允许值 {maximumRate:G6}/s");
    }

    private DetectorCandidate? EvaluateStatisticalBaseline(
        PlcAnomalyRule rule,
        SignalRuleState state,
        double value)
    {
        if (!rule.StatisticalBaselineEnabled || state.NumericWindow.Count < _options.MinimumSamples)
            return null;

        var values = state.NumericWindow.ToArray();
        Array.Sort(values);
        var median = Median(values);
        var deviations = values.Select(item => Math.Abs(item - median)).ToArray();
        Array.Sort(deviations);
        var mad = Median(deviations);
        var scale = Math.Max(
            Math.Max(rule.MinimumMad, Math.Abs(median) * 0.001),
            1.4826 * mad);
        var robustZ = Math.Abs(value - median) / scale;
        if (robustZ < rule.MadMultiplier) return null;

        var excess = robustZ / Math.Max(rule.MadMultiplier, double.Epsilon) - 1.0;
        var radius = rule.MadMultiplier * scale;
        return new DetectorCandidate(
            PlcAnomalyType.StatisticalBaseline,
            "MedianMadDetector",
            ScoreFromExceedance(excess),
            value,
            median,
            median - radius,
            median + radius,
            $"当前值偏离动态中位数基线，Robust-Z={robustZ:F2}，阈值={rule.MadMultiplier:F2}");
    }

    private static DetectorCandidate? EvaluateDuration(
        PlcAnomalyRule rule,
        PlcAnomalySample sample,
        DateTime trueSinceUtc,
        DateTime nowUtc)
    {
        if (rule.MaximumTrueDurationMs is not { } maximumDurationMs || sample.BooleanValue != true)
            return null;

        var durationMs = Math.Max(0, (nowUtc - trueSinceUtc).TotalMilliseconds);
        if (durationMs <= maximumDurationMs) return null;

        var ratio = durationMs / Math.Max(maximumDurationMs, 1) - 1.0;
        return new DetectorCandidate(
            PlcAnomalyType.Duration,
            "DurationDetector",
            ScoreFromExceedance(ratio),
            durationMs,
            maximumDurationMs,
            0,
            maximumDurationMs,
            $"信号保持 true 已 {durationMs:F0}ms，超过允许值 {maximumDurationMs}ms");
    }

    private void AddWindowValue(SignalRuleState state, double value)
    {
        state.NumericWindow.Enqueue(value);
        while (state.NumericWindow.Count > _options.WindowSize)
            state.NumericWindow.Dequeue();
    }

    private static double Median(double[] sorted)
    {
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private static double ScoreFromExceedance(double exceedance) =>
        Math.Clamp(0.85 + Math.Max(0, exceedance) * 0.15, 0.85, 1.0);

    private PlcAnomalySeverity ResolveSeverity(PlcAnomalySeverity configured, double score)
    {
        if (configured == PlcAnomalySeverity.Critical) return configured;
        if (score >= _options.AlarmThreshold && configured < PlcAnomalySeverity.Error)
            return PlcAnomalySeverity.Error;
        return configured;
    }

    private static bool RuleMatches(PlcAnomalyRule rule, PlcAnomalySample sample) =>
        rule.Enabled &&
        !string.IsNullOrWhiteSpace(rule.RuleId) &&
        !string.IsNullOrWhiteSpace(rule.SignalPattern) &&
        WildcardMatch(rule.PlcPattern, sample.PlcName) &&
        WildcardMatch(rule.DevicePattern, sample.DeviceId) &&
        WildcardMatch(rule.SignalPattern, sample.SignalName);

    private static bool WildcardMatch(string? pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;

        var p = 0;
        var v = 0;
        var star = -1;
        var match = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                p++;
                v++;
                continue;
            }

            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                match = v;
                continue;
            }

            if (star >= 0)
            {
                p = star + 1;
                v = ++match;
                continue;
            }

            return false;
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static string BuildStateKey(string ruleId, PlcAnomalySample sample) =>
        $"{ruleId}|{sample.PlcName}|{sample.DeviceId}|{sample.SignalName}";

    private static string BuildAlarmCode(string anomalyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(anomalyKey));
        return $"PLC_ANOM_{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }

    private static string CreateContextJson(
        PlcAnomalyRule rule,
        PlcAnomalySample sample,
        DetectorCandidate candidate) =>
        PlcAnomalyRecord.SerializeContext(new
        {
            rule.RuleId,
            rule.Description,
            sample.EventId,
            sample.Source,
            sample.OldValue,
            sample.NewValue,
            candidate.DetectorName,
            candidate.Score,
            candidate.ActualValue,
            candidate.ExpectedValue,
            candidate.LowerBound,
            candidate.UpperBound
        });

    private sealed class SignalRuleState
    {
        public SignalRuleState(PlcAnomalyRule rule) => Rule = rule;

        public object Gate { get; } = new();
        public PlcAnomalyRule Rule { get; }
        public Queue<double> NumericWindow { get; } = new();
        public double? LastNumericValue { get; set; }
        public DateTime? LastNumericUtc { get; set; }
        public DateTime? TrueSinceUtc { get; set; }
        public PlcAnomalySample? LastSample { get; set; }
        public int AbnormalCount { get; set; }
        public int NormalCount { get; set; }
        public DateTime? FirstAbnormalUtc { get; set; }
        public PlcAnomalyRecord? Active { get; set; }
    }

    private sealed record DetectorCandidate(
        PlcAnomalyType Type,
        string DetectorName,
        double Score,
        double? ActualValue,
        double? ExpectedValue,
        double? LowerBound,
        double? UpperBound,
        string Reason);

    private sealed record AnomalyTransition(bool IsDetected, PlcAnomalyRecord Record);
}
