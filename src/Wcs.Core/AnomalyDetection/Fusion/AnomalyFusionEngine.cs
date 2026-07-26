namespace Wcs.Core.AnomalyDetection.Fusion;

using System.Collections.Concurrent;
using Wcs.Core.AnomalyDetection;

/// <summary>
/// 将同一资产的多来源异常证据进行有界、可解释融合。
/// 同一来源只采用贡献最大的活动证据，避免同一检测器高频采样重复放大。
/// </summary>
public sealed class AnomalyFusionEngine : IAnomalyFusionEngine
{
    private readonly AnomalyFusionOptions _options;
    private readonly IReadOnlyDictionary<string, AnomalyFusionSourcePolicy> _policies;
    private readonly ConcurrentDictionary<string, AssetState> _assets = new(StringComparer.Ordinal);
    private readonly object _historyGate = new();
    private readonly Queue<FusedHealthSnapshot> _history = new();
    private long _evidenceAccepted;
    private long _evidenceRecovered;
    private long _evidenceExpired;
    private long _evidenceDropped;
    private long _evaluations;
    private long _warningTransitions;
    private long _alarmTransitions;
    private long _recoveryTransitions;

    public AnomalyFusionEngine(AnomalyFusionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policies = BuildPolicies(options.Sources);
    }

    public void Process(AnomalyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!_options.Enabled) return;
        if (string.IsNullOrWhiteSpace(evidence.AssetId) ||
            string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
            string.IsNullOrWhiteSpace(evidence.Source))
        {
            Interlocked.Increment(ref _evidenceDropped);
            return;
        }

        if (!_assets.TryGetValue(evidence.AssetId, out var state))
        {
            if (evidence.State == AnomalyEvidenceState.Recovered) return;
            if (_assets.Count >= _options.MaximumTrackedAssets)
            {
                Interlocked.Increment(ref _evidenceDropped);
                return;
            }
            state = _assets.GetOrAdd(evidence.AssetId, static assetId => new AssetState(assetId));
        }

        FusedHealthSnapshot? transitionSnapshot;
        lock (state.Gate)
        {
            if (evidence.State == AnomalyEvidenceState.Active)
            {
                if (!state.Evidence.ContainsKey(evidence.EvidenceId) &&
                    state.Evidence.Count >= _options.MaximumEvidencePerAsset &&
                    !EvictOldestRecoveredLocked(state))
                {
                    Interlocked.Increment(ref _evidenceDropped);
                    return;
                }

                state.Evidence[evidence.EvidenceId] = NormalizeEvidence(evidence);
                state.FirstObservedAtUtc = state.FirstObservedAtUtc == default
                    ? evidence.ObservedAtUtc
                    : MinUtc(state.FirstObservedAtUtc, evidence.ObservedAtUtc);
                Interlocked.Increment(ref _evidenceAccepted);
            }
            else
            {
                if (state.Evidence.TryGetValue(evidence.EvidenceId, out var existing))
                {
                    state.Evidence[evidence.EvidenceId] = existing with
                    {
                        State = AnomalyEvidenceState.Recovered,
                        ObservedAtUtc = evidence.ObservedAtUtc,
                        ExpiresAtUtc = evidence.ExpiresAtUtc ??
                            evidence.ObservedAtUtc.AddSeconds(_options.RecoveredEvidenceRetentionSeconds)
                    };
                    Interlocked.Increment(ref _evidenceRecovered);
                }
            }

            transitionSnapshot = EvaluateLocked(state, evidence.ObservedAtUtc);
        }

        if (transitionSnapshot is not null) AddHistory(transitionSnapshot);
    }

    public void Maintenance(DateTime utcNow)
    {
        if (!_options.Enabled) return;
        foreach (var pair in _assets)
        {
            var state = pair.Value;
            FusedHealthSnapshot? transitionSnapshot = null;
            var removeAsset = false;
            lock (state.Gate)
            {
                var changed = false;
                foreach (var evidencePair in state.Evidence.ToArray())
                {
                    var evidence = evidencePair.Value;
                    var expiry = evidence.ExpiresAtUtc ??
                        evidence.ObservedAtUtc.AddSeconds(
                            evidence.State == AnomalyEvidenceState.Active
                                ? _options.EvidenceRetentionSeconds
                                : _options.RecoveredEvidenceRetentionSeconds);
                    if (expiry > utcNow) continue;
                    state.Evidence.Remove(evidencePair.Key);
                    changed = true;
                    if (evidence.State == AnomalyEvidenceState.Active)
                        Interlocked.Increment(ref _evidenceExpired);
                }

                if (changed) transitionSnapshot = EvaluateLocked(state, utcNow);
                removeAsset = state.Evidence.Count == 0 &&
                    state.Status == FusedHealthStatus.Normal &&
                    utcNow - state.LastEvaluatedAtUtc >
                        TimeSpan.FromSeconds(_options.InactiveStateRetentionSeconds);
            }

            if (transitionSnapshot is not null) AddHistory(transitionSnapshot);
            if (removeAsset)
                ((ICollection<KeyValuePair<string, AssetState>>)_assets).Remove(pair);
        }
    }

    public FusedHealthSnapshot? GetAsset(string assetId)
    {
        if (!_assets.TryGetValue(assetId, out var state)) return null;
        lock (state.Gate) return BuildSnapshotLocked(state);
    }

    public IReadOnlyList<FusedHealthSnapshot> GetAssets(
        FusedHealthStatus? minimumStatus = null,
        int maximumCount = 200)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 10_000);
        var snapshots = new List<FusedHealthSnapshot>();
        foreach (var state in _assets.Values)
        {
            lock (state.Gate)
            {
                var snapshot = BuildSnapshotLocked(state);
                if (minimumStatus is not null && snapshot.Status < minimumStatus) continue;
                snapshots.Add(snapshot);
            }
        }
        return snapshots
            .OrderByDescending(static snapshot => snapshot.Status)
            .ThenByDescending(static snapshot => snapshot.Score)
            .ThenBy(static snapshot => snapshot.AssetId, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
    }

    public AnomalyFusionStatus GetStatus()
    {
        var activeEvidence = 0;
        foreach (var state in _assets.Values)
        {
            lock (state.Gate)
                activeEvidence += state.Evidence.Values.Count(static evidence =>
                    evidence.State == AnomalyEvidenceState.Active);
        }

        int historyCount;
        lock (_historyGate) historyCount = _history.Count;
        return new AnomalyFusionStatus
        {
            Enabled = _options.Enabled,
            TrackedAssets = _assets.Count,
            ActiveEvidence = activeEvidence,
            RetainedSnapshots = historyCount,
            EvidenceAccepted = Interlocked.Read(ref _evidenceAccepted),
            EvidenceRecovered = Interlocked.Read(ref _evidenceRecovered),
            EvidenceExpired = Interlocked.Read(ref _evidenceExpired),
            EvidenceDropped = Interlocked.Read(ref _evidenceDropped),
            Evaluations = Interlocked.Read(ref _evaluations),
            WarningTransitions = Interlocked.Read(ref _warningTransitions),
            AlarmTransitions = Interlocked.Read(ref _alarmTransitions),
            RecoveryTransitions = Interlocked.Read(ref _recoveryTransitions)
        };
    }

    private FusedHealthSnapshot? EvaluateLocked(AssetState state, DateTime utcNow)
    {
        Interlocked.Increment(ref _evaluations);
        var activeBySource = state.Evidence.Values
            .Where(static evidence => evidence.State == AnomalyEvidenceState.Active)
            .GroupBy(static evidence => evidence.Source, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(GetContribution)
                .ThenByDescending(static evidence => evidence.ObservedAtUtc)
                .First())
            .OrderByDescending(GetContribution)
            .ToArray();

        var independentSources = activeBySource.Length;
        var product = 1.0;
        foreach (var evidence in activeBySource)
            product *= 1.0 - GetContribution(evidence);
        var score = 1.0 - product;
        if (independentSources > 1)
        {
            score += Math.Min(
                _options.MaximumSourceDiversityBonus,
                (independentSources - 1) * _options.SourceDiversityBonus);
        }
        score = Math.Clamp(score, 0, 1);

        var requested = ResolveRequestedStatus(score, independentSources);
        var oldStatus = state.Status;
        if (requested == FusedHealthStatus.Alarm)
        {
            state.AlarmCount++;
            state.WarningCount++;
            state.RecoveryCount = 0;
            if (state.AlarmCount >= _options.ConsecutiveAlarmEvaluations)
                state.Status = FusedHealthStatus.Alarm;
            else if (state.Status < FusedHealthStatus.Warning &&
                     state.WarningCount >= _options.ConsecutiveWarningEvaluations)
                state.Status = FusedHealthStatus.Warning;
        }
        else if (requested == FusedHealthStatus.Warning)
        {
            state.WarningCount++;
            state.AlarmCount = 0;
            state.RecoveryCount = 0;
            if (state.Status < FusedHealthStatus.Warning &&
                state.WarningCount >= _options.ConsecutiveWarningEvaluations)
                state.Status = FusedHealthStatus.Warning;
        }
        else if (score <= _options.RecoveryThreshold || independentSources == 0)
        {
            state.WarningCount = 0;
            state.AlarmCount = 0;
            if (state.Status >= FusedHealthStatus.Warning)
            {
                state.RecoveryCount++;
                if (state.RecoveryCount >= _options.ConsecutiveRecoveryEvaluations)
                {
                    state.Status = FusedHealthStatus.Normal;
                    state.RecoveryCount = 0;
                }
            }
            else
            {
                state.Status = requested;
                state.RecoveryCount = 0;
            }
        }
        else
        {
            // Warning/Alarm 使用滞回：分数未降到恢复阈值前保持当前正式状态。
            state.WarningCount = 0;
            state.AlarmCount = 0;
            state.RecoveryCount = 0;
            if (state.Status < FusedHealthStatus.Warning)
                state.Status = requested;
        }

        state.Score = score;
        state.LastEvaluatedAtUtc = utcNow;
        state.ActiveEvidence = activeBySource;
        if (oldStatus == state.Status) return null;

        if (state.Status == FusedHealthStatus.Warning)
            Interlocked.Increment(ref _warningTransitions);
        else if (state.Status == FusedHealthStatus.Alarm)
            Interlocked.Increment(ref _alarmTransitions);
        else if (oldStatus >= FusedHealthStatus.Warning && state.Status == FusedHealthStatus.Normal)
            Interlocked.Increment(ref _recoveryTransitions);
        return BuildSnapshotLocked(state);
    }

    private FusedHealthStatus ResolveRequestedStatus(double score, int independentSources)
    {
        if (score >= _options.AlarmThreshold &&
            independentSources >= _options.MinimumIndependentSourcesForAlarm)
            return FusedHealthStatus.Alarm;
        if (score >= _options.WarningThreshold) return FusedHealthStatus.Warning;
        if (score >= _options.ObserveThreshold) return FusedHealthStatus.Observe;
        return FusedHealthStatus.Normal;
    }

    private FusedHealthSnapshot BuildSnapshotLocked(AssetState state)
    {
        var evidence = state.ActiveEvidence
            .Select(item => new FusedEvidenceSummary
            {
                EvidenceId = item.EvidenceId,
                Source = item.Source,
                Category = item.Category,
                Score = item.Score,
                Confidence = item.Confidence,
                Contribution = GetContribution(item),
                Severity = item.Severity,
                ObservedAtUtc = item.ObservedAtUtc,
                RelatedEntityId = item.RelatedEntityId,
                Reason = item.Reason
            })
            .ToArray();
        return new FusedHealthSnapshot
        {
            AssetId = state.AssetId,
            Status = state.Status,
            Score = state.Score,
            IndependentSourceCount = evidence.Select(static item => item.Source).Distinct(StringComparer.Ordinal).Count(),
            FirstObservedAtUtc = state.FirstObservedAtUtc == default
                ? state.LastEvaluatedAtUtc
                : state.FirstObservedAtUtc,
            LastEvaluatedAtUtc = state.LastEvaluatedAtUtc,
            Evidence = evidence,
            Summary = evidence.Length == 0
                ? "无活动异常证据。"
                : string.Join("；", evidence.Take(3).Select(static item =>
                    $"{item.Source}:{item.Category}({item.Contribution:F2})"))
        };
    }

    private double GetContribution(AnomalyEvidence evidence)
    {
        var policy = _policies.TryGetValue(evidence.Source, out var configured)
            ? configured
            : new AnomalyFusionSourcePolicy
            {
                Source = evidence.Source,
                Weight = 1,
                DefaultConfidence = 0.7
            };
        var confidence = evidence.Confidence > 0
            ? evidence.Confidence
            : policy.DefaultConfidence;
        var severityFloor = evidence.Severity switch
        {
            PlcAnomalySeverity.Observe => 0.25,
            PlcAnomalySeverity.Warning => 0.55,
            PlcAnomalySeverity.Error => 0.75,
            PlcAnomalySeverity.Critical => 0.90,
            _ => 0
        };
        var score = Math.Max(Math.Clamp(evidence.Score, 0, 1), severityFloor);
        return Math.Clamp(score * Math.Clamp(confidence, 0, 1) * policy.Weight, 0, 0.99);
    }

    private AnomalyEvidence NormalizeEvidence(AnomalyEvidence evidence)
    {
        var policy = _policies.TryGetValue(evidence.Source, out var configured)
            ? configured
            : null;
        return evidence with
        {
            Source = evidence.Source.Trim(),
            AssetId = evidence.AssetId.Trim(),
            Score = Math.Clamp(evidence.Score, 0, 1),
            Confidence = Math.Clamp(
                evidence.Confidence > 0
                    ? evidence.Confidence
                    : policy?.DefaultConfidence ?? 0.7,
                0,
                1),
            ExpiresAtUtc = evidence.ExpiresAtUtc ??
                evidence.ObservedAtUtc.AddSeconds(_options.EvidenceRetentionSeconds)
        };
    }

    private static IReadOnlyDictionary<string, AnomalyFusionSourcePolicy> BuildPolicies(
        IReadOnlyCollection<AnomalyFusionSourcePolicy> configured)
    {
        var policies = DefaultPolicies().ToDictionary(policy => policy.Source, StringComparer.Ordinal);
        foreach (var policy in configured)
        {
            if (string.IsNullOrWhiteSpace(policy.Source)) continue;
            policies[policy.Source.Trim()] = new AnomalyFusionSourcePolicy
            {
                Source = policy.Source.Trim(),
                Weight = Math.Clamp(policy.Weight, 0, 2),
                DefaultConfidence = Math.Clamp(policy.DefaultConfidence, 0, 1)
            };
        }
        return policies;
    }

    private static IEnumerable<AnomalyFusionSourcePolicy> DefaultPolicies()
    {
        yield return Policy(AnomalyEvidenceSources.ThresholdRule, 1.0, 0.95);
        yield return Policy(AnomalyEvidenceSources.RateRule, 0.9, 0.90);
        yield return Policy(AnomalyEvidenceSources.DurationRule, 1.0, 0.95);
        yield return Policy(AnomalyEvidenceSources.StatisticalRule, 0.8, 0.75);
        yield return Policy(AnomalyEvidenceSources.ConsistencyRule, 1.1, 0.98);
        yield return Policy(AnomalyEvidenceSources.IsolationForest, 0.85, 0.75);
        yield return Policy(AnomalyEvidenceSources.PeerMedianMad, 0.9, 0.82);
        yield return Policy(AnomalyEvidenceSources.CycleSequence, 1.1, 0.98);
        yield return Policy(AnomalyEvidenceSources.CyclePhaseDuration, 0.9, 0.85);
        yield return Policy(AnomalyEvidenceSources.CycleTotalDuration, 0.9, 0.85);
    }

    private static AnomalyFusionSourcePolicy Policy(string source, double weight, double confidence) => new()
    {
        Source = source,
        Weight = weight,
        DefaultConfidence = confidence
    };

    private static bool EvictOldestRecoveredLocked(AssetState state)
    {
        var oldest = state.Evidence.Values
            .Where(static evidence => evidence.State == AnomalyEvidenceState.Recovered)
            .OrderBy(static evidence => evidence.ObservedAtUtc)
            .FirstOrDefault();
        return oldest is not null && state.Evidence.Remove(oldest.EvidenceId);
    }

    private void AddHistory(FusedHealthSnapshot snapshot)
    {
        lock (_historyGate)
        {
            _history.Enqueue(snapshot);
            while (_history.Count > _options.MaximumSnapshots) _history.Dequeue();
        }
    }

    private static DateTime MinUtc(DateTime left, DateTime right)
    {
        if (left == default) return right;
        if (right == default) return left;
        return left <= right ? left : right;
    }

    private sealed class AssetState
    {
        public AssetState(string assetId) => AssetId = assetId;
        public object Gate { get; } = new();
        public string AssetId { get; }
        public Dictionary<string, AnomalyEvidence> Evidence { get; } = new(StringComparer.Ordinal);
        public AnomalyEvidence[] ActiveEvidence { get; set; } = Array.Empty<AnomalyEvidence>();
        public FusedHealthStatus Status { get; set; }
        public double Score { get; set; }
        public DateTime FirstObservedAtUtc { get; set; }
        public DateTime LastEvaluatedAtUtc { get; set; }
        public int WarningCount { get; set; }
        public int AlarmCount { get; set; }
        public int RecoveryCount { get; set; }
    }
}
