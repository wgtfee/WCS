namespace Wcs.Core.AnomalyDetection;

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// PLC anomaly detection engine v1.1: threshold, rate, duration,
/// cross-signal consistency and Median/MAD dynamic baseline.
/// Inactive per-signal state and related-signal snapshots are bounded by TTL.
/// </summary>
public sealed class PlcAnomalyEngine : IPlcAnomalyEngine, IPlcAnomalyStatusProvider
{
    private const string ModelVersion = "rules-mad-v1.1";

    private readonly PlcAnomalyOptions _options;
    private readonly IEventBus _eventBus;
    private readonly ConcurrentDictionary<string, SignalRuleState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PlcAnomalyRecord> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceSampleCache> _latestSamplesByDevice = new(StringComparer.Ordinal);

    private long _processedSamples;
    private long _matchedRuleEvaluations;
    private long _detectorObservations;
    private long _raised;
    private long _recovered;
    private long _suppressed;
    private long _failures;
    private long _evictedRuleStates;
    private long _evictedRelatedSamples;
    private long _evictedDeviceSnapshots;
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
        UpdateMaximum(ref _lastProcessedTicks, sample.TimestampUtc.Ticks);

        try
        {
            List<AnomalyTransition>? transitions = null;
            DeviceSampleCache? deviceCache = null;

            foreach (var rule in _options.Rules)
            {
                if (IsConsistencyRule(rule))
                {
                    if (!ConsistencyRuleRelevant(rule, sample)) continue;
                    Interlocked.Increment(ref _matchedRuleEvaluations);

                    deviceCache ??= StoreRelatedSample(sample);
                    var transition = EvaluateConsistencyState(rule, sample, deviceCache);
                    if (transition is not null)
                    {
                        transitions ??= new List<AnomalyTransition>();
                        transitions.Add(transition);
                    }
                    continue;
                }

                if (!RuleMatches(rule, sample)) continue;
                Interlocked.Increment(ref _matchedRuleEvaluations);

                var normalTransition = EvaluateNormalState(rule, sample);
                if (normalTransition is not null)
                {
                    transitions ??= new List<AnomalyTransition>();
                    transitions.Add(normalTransition);
                }
            }

            if (transitions is not null)
            {
                foreach (var transition in transitions)
                    await PublishTransitionAsync(transition, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Evaluates duration rules and performs bounded cleanup of inactive states
    /// and expired related-signal snapshots.
    /// </summary>
    public async ValueTask SweepAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        List<AnomalyTransition>? transitions = null;
        foreach (var pair in _states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = pair.Value;
            if (state.Rule.MaximumTrueDurationMs is null || IsConsistencyRule(state.Rule)) continue;

            AnomalyTransition? transition = null;
            lock (state.Gate)
            {
                if (state.IsRetired || state.LastSample?.BooleanValue != true || state.TrueSinceUtc is null)
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
                await PublishTransitionAsync(transition, cancellationToken).ConfigureAwait(false);
        }

        CleanupInactiveStates(utcNow, _options.MaximumCleanupItemsPerSweep);
        CleanupRelatedSamples(utcNow, _options.MaximumCleanupItemsPerSweep);
    }

    public IReadOnlyList<PlcAnomalyRecord> GetActiveAnomalies() =>
        _active.Values.OrderByDescending(static item => item.StartTimeUtc).ToList();

    public PlcAnomalyStatus GetStatus()
    {
        var ticks = Volatile.Read(ref _lastProcessedTicks);
        var statisticalWindows = 0;
        foreach (var state in _states.Values)
        {
            if (state.NumericWindow is not null) statisticalWindows++;
        }

        var relatedSamples = 0;
        foreach (var cache in _latestSamplesByDevice.Values)
            relatedSamples += cache.Samples.Count;

        return new PlcAnomalyStatus
        {
            Enabled = _options.Enabled,
            ConfiguredRules = _options.Rules.Count,
            ProcessedSamples = Interlocked.Read(ref _processedSamples),
            MatchedRuleEvaluations = Interlocked.Read(ref _matchedRuleEvaluations),
            DetectorObservations = Interlocked.Read(ref _detectorObservations),
            Raised = Interlocked.Read(ref _raised),
            Recovered = Interlocked.Read(ref _recovered),
            Suppressed = Interlocked.Read(ref _suppressed),
            Failures = Interlocked.Read(ref _failures),
            TrackedRuleSignals = _states.Count,
            StatisticalWindows = statisticalWindows,
            TrackedDeviceSnapshots = _latestSamplesByDevice.Count,
            TrackedRelatedSamples = relatedSamples,
            EvictedRuleStates = Interlocked.Read(ref _evictedRuleStates),
            EvictedRelatedSamples = Interlocked.Read(ref _evictedRelatedSamples),
            EvictedDeviceSnapshots = Interlocked.Read(ref _evictedDeviceSnapshots),
            ActiveAnomalies = _active.Count,
            LastProcessedUtc = ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc),
            LastError = Volatile.Read(ref _lastError)
        };
    }

    private AnomalyTransition? EvaluateNormalState(PlcAnomalyRule rule, PlcAnomalySample sample)
    {
        var stateKey = BuildStateKey(rule.RuleId, sample);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = GetOrCreateState(stateKey, rule, sample.TimestampUtc);
            if (state is null) return null;
            state.Touch(sample.TimestampUtc);

            lock (state.Gate)
            {
                if (state.IsRetired) continue;
                return EvaluateSampleLocked(state, sample);
            }
        }

        Interlocked.Increment(ref _suppressed);
        return null;
    }

    private AnomalyTransition? EvaluateConsistencyState(
        PlcAnomalyRule rule,
        PlcAnomalySample sample,
        DeviceSampleCache deviceCache)
    {
        var stateKey = BuildConsistencyStateKey(rule.RuleId, sample);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = GetOrCreateState(stateKey, rule, sample.TimestampUtc);
            if (state is null) return null;
            state.Touch(sample.TimestampUtc);

            lock (state.Gate)
            {
                if (state.IsRetired) continue;

                var evaluation = EvaluateConsistency(rule, sample, deviceCache.Samples);
                var anchor = evaluation.Anchor with
                {
                    EventId = sample.EventId,
                    TimestampUtc = sample.TimestampUtc,
                    TaskId = sample.TaskId ?? evaluation.Anchor.TaskId
                };
                if (evaluation.Candidate is not null)
                    Interlocked.Increment(ref _detectorObservations);

                var transition = ApplyCandidateLocked(state, anchor, evaluation.Candidate);
                state.LastSample = anchor;
                return transition;
            }
        }

        Interlocked.Increment(ref _suppressed);
        return null;
    }

    private SignalRuleState? GetOrCreateState(
        string stateKey,
        PlcAnomalyRule rule,
        DateTime sampleUtc)
    {
        if (_states.TryGetValue(stateKey, out var existing) && !existing.IsRetired)
            return existing;

        if (_states.Count >= _options.MaximumTrackedRuleSignals)
        {
            CleanupInactiveStates(sampleUtc, Math.Min(_options.MaximumCleanupItemsPerSweep, 1_000));
            if (_states.Count >= _options.MaximumTrackedRuleSignals)
            {
                Interlocked.Increment(ref _suppressed);
                return null;
            }
        }

        var created = new SignalRuleState(rule, _options.WindowSize, sampleUtc);
        return _states.GetOrAdd(stateKey, created);
    }

    private DeviceSampleCache StoreRelatedSample(PlcAnomalySample sample)
    {
        var deviceKey = BuildDeviceKey(sample);
        var cache = _latestSamplesByDevice.GetOrAdd(deviceKey, static _ => new DeviceSampleCache());
        cache.Touch(sample.TimestampUtc);
        cache.Samples[sample.SignalName] = sample;
        return cache;
    }

    private void CleanupInactiveStates(DateTime utcNow, int maximumItems)
    {
        if (maximumItems <= 0 || _states.IsEmpty) return;
        var cutoff = utcNow.AddSeconds(-Math.Max(1, _options.InactiveStateRetentionSeconds));
        var inspected = 0;
        var collection = (ICollection<KeyValuePair<string, SignalRuleState>>)_states;

        foreach (var pair in _states)
        {
            if (inspected++ >= maximumItems) break;
            var state = pair.Value;
            lock (state.Gate)
            {
                if (state.IsRetired ||
                    state.Active is not null ||
                    state.TrueSinceUtc is not null ||
                    state.LastTouchedUtc > cutoff)
                    continue;

                state.IsRetired = true;
                if (collection.Remove(pair))
                    Interlocked.Increment(ref _evictedRuleStates);
                else
                    state.IsRetired = false;
            }
        }
    }

    private void CleanupRelatedSamples(DateTime utcNow, int maximumItems)
    {
        if (maximumItems <= 0 || _latestSamplesByDevice.IsEmpty) return;
        var cutoff = utcNow.AddSeconds(-Math.Max(1, _options.RelatedSampleRetentionSeconds));
        var inspected = 0;
        var deviceCollection = (ICollection<KeyValuePair<string, DeviceSampleCache>>)_latestSamplesByDevice;

        foreach (var devicePair in _latestSamplesByDevice)
        {
            if (inspected >= maximumItems) break;
            var cache = devicePair.Value;
            var sampleCollection = (ICollection<KeyValuePair<string, PlcAnomalySample>>)cache.Samples;

            foreach (var samplePair in cache.Samples)
            {
                if (inspected++ >= maximumItems) break;
                if (samplePair.Value.TimestampUtc > cutoff) continue;
                if (sampleCollection.Remove(samplePair))
                    Interlocked.Increment(ref _evictedRelatedSamples);
            }

            if (cache.Samples.IsEmpty && cache.LastTouchedUtc <= cutoff && deviceCollection.Remove(devicePair))
                Interlocked.Increment(ref _evictedDeviceSnapshots);
        }
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

            // Allocate and update the baseline window only for rules that use it.
            if (candidate is null)
                state.NumericWindow?.Add(current);
        }

        return transition;
    }

    private ConsistencyEvaluation EvaluateConsistency(
        PlcAnomalyRule rule,
        PlcAnomalySample current,
        ConcurrentDictionary<string, PlcAnomalySample> deviceSamples)
    {
        var primary = FindLatest(deviceSamples, rule.SignalPattern) ?? current;
        var related = FindLatest(deviceSamples, rule.RelatedSignalPattern!);

        if (!ValueEquals(primary, rule.WhenValueEquals ?? "true"))
            return new ConsistencyEvaluation(primary, null);

        if (related is null)
        {
            return new ConsistencyEvaluation(primary, new DetectorCandidate(
                PlcAnomalyType.Consistency,
                "ConsistencyDetector",
                1.0,
                null,
                null,
                rule.RelatedMinimum,
                rule.RelatedMaximum,
                $"条件信号 {primary.SignalName}={primary.NewValue}，但尚未获得关联信号 {rule.RelatedSignalPattern}"));
        }

        var ageMs = Math.Max(0, (current.TimestampUtc - related.TimestampUtc).TotalMilliseconds);
        if (ageMs > rule.MaximumRelatedAgeMs)
        {
            return new ConsistencyEvaluation(primary, new DetectorCandidate(
                PlcAnomalyType.Consistency,
                "ConsistencyDetector",
                1.0,
                ToNumeric(related),
                null,
                rule.RelatedMinimum,
                rule.RelatedMaximum,
                $"关联信号 {related.SignalName} 已超过 {ageMs:F0}ms 未更新，允许值 {rule.MaximumRelatedAgeMs}ms"));
        }

        var mismatch = false;
        if (!string.IsNullOrWhiteSpace(rule.RelatedExpectedValue) &&
            !ValueEquals(related, rule.RelatedExpectedValue))
            mismatch = true;

        if (rule.RelatedMinimum is { } minimum &&
            (related.NumericValue is null || related.NumericValue.Value < minimum))
            mismatch = true;

        if (rule.RelatedMaximum is { } maximum &&
            (related.NumericValue is null || related.NumericValue.Value > maximum))
            mismatch = true;

        if (!mismatch) return new ConsistencyEvaluation(primary, null);

        var actual = ToNumeric(related);
        var expected = rule.RelatedExpectedValue is not null &&
                       double.TryParse(rule.RelatedExpectedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : rule.RelatedMinimum ?? rule.RelatedMaximum;
        return new ConsistencyEvaluation(primary, new DetectorCandidate(
            PlcAnomalyType.Consistency,
            "ConsistencyDetector",
            0.95,
            actual,
            expected,
            rule.RelatedMinimum,
            rule.RelatedMaximum,
            $"{primary.SignalName}={primary.NewValue} 时，关联信号 {related.SignalName}={related.NewValue} 不满足预期"));
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
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _eventBus.PublishAsync(new PlcAnomalyRecoveredEvent
            {
                Anomaly = transition.Record
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private PlcAnomalyRecord CreateRecord(
        PlcAnomalyRule rule,
        PlcAnomalySample sample,
        DetectorCandidate candidate,
        DateTime startUtc)
    {
        var anomalyKey = IsConsistencyRule(rule)
            ? BuildConsistencyStateKey(rule.RuleId, sample)
            : BuildStateKey(rule.RuleId, sample);
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
        var window = state.NumericWindow;
        if (!rule.StatisticalBaselineEnabled || window is null || window.Count < _options.MinimumSamples)
            return null;

        var values = window.Snapshot();
        Array.Sort(values);
        var median = Median(values);
        var deviations = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
            deviations[index] = Math.Abs(values[index] - median);
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

    private static bool IsConsistencyRule(PlcAnomalyRule rule) =>
        !string.IsNullOrWhiteSpace(rule.RelatedSignalPattern);

    private static bool RuleMatches(PlcAnomalyRule rule, PlcAnomalySample sample) =>
        RuleScopeMatches(rule, sample) &&
        !string.IsNullOrWhiteSpace(rule.SignalPattern) &&
        WildcardMatch(rule.SignalPattern, sample.SignalName);

    private static bool ConsistencyRuleRelevant(PlcAnomalyRule rule, PlcAnomalySample sample) =>
        RuleScopeMatches(rule, sample) &&
        (WildcardMatch(rule.SignalPattern, sample.SignalName) ||
         WildcardMatch(rule.RelatedSignalPattern, sample.SignalName));

    private static bool RuleScopeMatches(PlcAnomalyRule rule, PlcAnomalySample sample) =>
        rule.Enabled &&
        !string.IsNullOrWhiteSpace(rule.RuleId) &&
        WildcardMatch(rule.PlcPattern, sample.PlcName) &&
        WildcardMatch(rule.DevicePattern, sample.DeviceId);

    private static PlcAnomalySample? FindLatest(
        ConcurrentDictionary<string, PlcAnomalySample> samples,
        string pattern) =>
        samples.Values
            .Where(item => WildcardMatch(pattern, item.SignalName))
            .OrderByDescending(static item => item.TimestampUtc)
            .FirstOrDefault();

    private static bool WildcardMatch(string? pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
        return FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);
    }

    private static bool ValueEquals(PlcAnomalySample sample, string expected)
    {
        if (bool.TryParse(expected, out var expectedBoolean) && sample.BooleanValue is { } actualBoolean)
            return actualBoolean == expectedBoolean;
        if (double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumeric) &&
            sample.NumericValue is { } actualNumeric)
            return Math.Abs(actualNumeric - expectedNumeric) <= Math.Max(1e-9, Math.Abs(expectedNumeric) * 1e-9);
        return string.Equals(sample.NewValue, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static double? ToNumeric(PlcAnomalySample sample) =>
        sample.NumericValue ?? (sample.BooleanValue is { } boolean ? boolean ? 1.0 : 0.0 : null);

    private static string BuildDeviceKey(PlcAnomalySample sample) =>
        $"{sample.PlcName}|{sample.DeviceId}";

    private static string BuildStateKey(string ruleId, PlcAnomalySample sample) =>
        $"{ruleId}|{sample.PlcName}|{sample.DeviceId}|{sample.SignalName}";

    private static string BuildConsistencyStateKey(string ruleId, PlcAnomalySample sample) =>
        $"{ruleId}|{sample.PlcName}|{sample.DeviceId}|CONSISTENCY";

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
            rule.RelatedSignalPattern,
            rule.WhenValueEquals,
            rule.RelatedExpectedValue,
            rule.RelatedMinimum,
            rule.RelatedMaximum,
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

    private static void UpdateMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    private sealed class SignalRuleState
    {
        private long _lastTouchedTicks;

        public SignalRuleState(PlcAnomalyRule rule, int windowSize, DateTime createdUtc)
        {
            Rule = rule;
            NumericWindow = rule.StatisticalBaselineEnabled
                ? new FixedDoubleWindow(windowSize)
                : null;
            _lastTouchedTicks = createdUtc.Ticks;
        }

        public object Gate { get; } = new();
        public PlcAnomalyRule Rule { get; }
        public FixedDoubleWindow? NumericWindow { get; }
        public double? LastNumericValue { get; set; }
        public DateTime? LastNumericUtc { get; set; }
        public DateTime? TrueSinceUtc { get; set; }
        public PlcAnomalySample? LastSample { get; set; }
        public int AbnormalCount { get; set; }
        public int NormalCount { get; set; }
        public DateTime? FirstAbnormalUtc { get; set; }
        public PlcAnomalyRecord? Active { get; set; }
        public bool IsRetired { get; set; }
        public DateTime LastTouchedUtc => new(Volatile.Read(ref _lastTouchedTicks), DateTimeKind.Utc);

        public void Touch(DateTime utc) => UpdateMaximum(ref _lastTouchedTicks, utc.Ticks);
    }

    private sealed class DeviceSampleCache
    {
        private long _lastTouchedTicks;
        public ConcurrentDictionary<string, PlcAnomalySample> Samples { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public DateTime LastTouchedUtc
        {
            get
            {
                var ticks = Volatile.Read(ref _lastTouchedTicks);
                return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
            }
        }
        public void Touch(DateTime utc) => UpdateMaximum(ref _lastTouchedTicks, utc.Ticks);
    }

    private sealed class FixedDoubleWindow
    {
        private readonly double[] _values;
        private int _count;
        private int _next;

        public FixedDoubleWindow(int capacity)
        {
            _values = new double[Math.Max(1, capacity)];
        }

        public int Count => _count;

        public void Add(double value)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }

        public double[] Snapshot()
        {
            var result = new double[_count];
            if (_count == 0) return result;
            var start = _count == _values.Length ? _next : 0;
            for (var index = 0; index < _count; index++)
                result[index] = _values[(start + index) % _values.Length];
            return result;
        }
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

    private sealed record ConsistencyEvaluation(
        PlcAnomalySample Anchor,
        DetectorCandidate? Candidate);

    private sealed record AnomalyTransition(bool IsDetected, PlcAnomalyRecord Record);
}
