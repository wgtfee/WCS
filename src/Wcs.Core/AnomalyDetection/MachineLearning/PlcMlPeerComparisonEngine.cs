namespace Wcs.Core.AnomalyDetection.MachineLearning;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// 在同一 Profile、同一运行上下文、同一时间窗口内，对同类设备特征进行 Median/MAD 横向比较。
/// 该检测器不训练黑盒模型，结果可解释并复用现有治理、异常生命周期和 AlarmCenter 链路。
/// </summary>
public sealed class PlcMlPeerComparisonEngine
{
    private const double MadScale = 1.4826;
    private readonly PlcMlAnomalyOptions _options;
    private readonly IReadOnlyDictionary<string, PlcMlProfile> _profiles;
    private readonly IPlcMlGovernanceStore _governanceStore;
    private readonly IEventBus _eventBus;
    private readonly ConcurrentDictionary<string, PeerBucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PeerState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PeerMetrics> _metrics = new(StringComparer.Ordinal);

    public PlcMlPeerComparisonEngine(
        PlcMlAnomalyOptions options,
        IPlcMlGovernanceStore governanceStore,
        IEventBus eventBus)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _governanceStore = governanceStore ?? throw new ArgumentNullException(nameof(governanceStore));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _profiles = options.Profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);
        foreach (var profile in options.Profiles)
            _metrics.TryAdd(profile.ProfileId, new PeerMetrics());
    }

    public void Add(PlcFeatureVector vector)
    {
        if (!_options.Enabled ||
            !_profiles.TryGetValue(vector.ProfileId, out var profile) ||
            !profile.Enabled ||
            !profile.PeerComparisonEnabled ||
            profile.DeploymentMode == PlcMlDeploymentMode.Disabled)
            return;

        if (_buckets.Count >= _options.MaximumTrackedWindows)
        {
            _metrics[profile.ProfileId].IncrementSkipped();
            return;
        }

        var key = BuildBucketKey(vector);
        var bucket = _buckets.GetOrAdd(
            key,
            _ => new PeerBucket(profile, vector.ContextKey, vector.WindowStartUtc, vector.WindowEndUtc));
        lock (bucket.Gate)
        {
            if (!vector.FeatureNames.SequenceEqual(bucket.FeatureNames ?? vector.FeatureNames, StringComparer.Ordinal))
            {
                _metrics[profile.ProfileId].RecordFailure(new InvalidOperationException(
                    $"Profile {profile.ProfileId} 同群窗口特征顺序不一致。"));
                return;
            }
            bucket.FeatureNames ??= vector.FeatureNames.ToArray();
            bucket.Vectors[$"{vector.PlcName}|{vector.DeviceId}"] = vector;
        }
    }

    public async Task FlushAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        foreach (var pair in _buckets)
        {
            var bucket = pair.Value;
            if (utcNow < bucket.WindowEndUtc.AddMilliseconds(bucket.Profile.PeerBucketWaitMs)) continue;
            if (!((ICollection<KeyValuePair<string, PeerBucket>>)_buckets).Remove(pair)) continue;
            await EvaluateBucketAsync(bucket, cancellationToken);
        }

        foreach (var pair in _states)
        {
            var state = pair.Value;
            var removable = false;
            lock (state.Gate)
            {
                removable = state.Active is null &&
                    utcNow - state.LastUpdatedUtc > TimeSpan.FromSeconds(state.Profile.PeerBucketRetentionSeconds);
            }
            if (removable)
                ((ICollection<KeyValuePair<string, PeerState>>)_states).Remove(pair);
        }

        foreach (var pair in _buckets)
        {
            if (utcNow - pair.Value.WindowEndUtc <= TimeSpan.FromSeconds(pair.Value.Profile.PeerBucketRetentionSeconds))
                continue;
            if (((ICollection<KeyValuePair<string, PeerBucket>>)_buckets).Remove(pair))
                _metrics[pair.Value.Profile.ProfileId].IncrementSkipped();
        }
    }

    public PlcMlPeerStatus GetStatus(string profileId)
    {
        _metrics.TryGetValue(profileId, out var metrics);
        metrics ??= new PeerMetrics();
        return new PlcMlPeerStatus
        {
            BucketsEvaluated = metrics.BucketsEvaluated,
            DevicesEvaluated = metrics.DevicesEvaluated,
            Raised = metrics.Raised,
            Recovered = metrics.Recovered,
            ShadowRaised = metrics.ShadowRaised,
            ActiveRaised = metrics.ActiveRaised,
            SkippedBuckets = metrics.SkippedBuckets,
            Failures = metrics.Failures,
            TrackedBuckets = _buckets.Values.Count(bucket =>
                string.Equals(bucket.Profile.ProfileId, profileId, StringComparison.Ordinal)),
            TrackedStates = _states.Values.Count(state =>
                string.Equals(state.Profile.ProfileId, profileId, StringComparison.Ordinal))
        };
    }

    private async Task EvaluateBucketAsync(PeerBucket bucket, CancellationToken cancellationToken)
    {
        var profile = bucket.Profile;
        var metrics = _metrics[profile.ProfileId];
        PlcFeatureVector[] vectors;
        string[] featureNames;
        lock (bucket.Gate)
        {
            vectors = bucket.Vectors.Values
                .OrderBy(static vector => vector.PlcName, StringComparer.Ordinal)
                .ThenBy(static vector => vector.DeviceId, StringComparer.Ordinal)
                .ToArray();
            featureNames = bucket.FeatureNames ?? Array.Empty<string>();
        }

        if (vectors.Length < profile.MinimumPeerDevices || featureNames.Length == 0)
        {
            metrics.IncrementSkipped();
            return;
        }

        try
        {
            var medians = new double[featureNames.Length];
            var mads = new double[featureNames.Length];
            for (var feature = 0; feature < featureNames.Length; feature++)
            {
                var values = vectors.Select(vector => vector.Values[feature]).OrderBy(static value => value).ToArray();
                medians[feature] = Median(values);
                var deviations = values
                    .Select(value => Math.Abs(value - medians[feature]))
                    .OrderBy(static value => value)
                    .ToArray();
                mads[feature] = Math.Max(Median(deviations) * MadScale, profile.MinimumPeerMad);
            }

            metrics.IncrementBucket(vectors.Length);
            foreach (var vector in vectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var maximumDeviation = 0.0;
                var maximumFeature = 0;
                for (var feature = 0; feature < featureNames.Length; feature++)
                {
                    var deviation = Math.Abs(vector.Values[feature] - medians[feature]) / mads[feature];
                    if (deviation <= maximumDeviation) continue;
                    maximumDeviation = deviation;
                    maximumFeature = feature;
                }

                var abnormal = maximumDeviation >= profile.PeerMadMultiplier;
                var stateKey = $"{profile.ProfileId}|{vector.PlcName}|{vector.DeviceId}";
                var state = _states.GetOrAdd(stateKey, _ => new PeerState(profile));
                PeerTransition? transition;
                var routed = ShouldRouteToActiveLifecycle(profile, vector.DeviceId);
                lock (state.Gate)
                {
                    transition = ApplyLocked(
                        state,
                        vector,
                        featureNames[maximumFeature],
                        vector.Values[maximumFeature],
                        medians[maximumFeature],
                        mads[maximumFeature],
                        maximumDeviation,
                        abnormal,
                        routed,
                        vectors.Length);
                    state.LastUpdatedUtc = vector.WindowEndUtc;
                }

                if (transition is null) continue;
                if (transition.IsDetected)
                {
                    metrics.IncrementRaised(transition.RoutedToActiveLifecycle);
                    await _governanceStore.UpsertCandidateAsync(
                        ToCandidate(profile, transition.Record, transition.RoutedToActiveLifecycle),
                        cancellationToken);
                    if (transition.RoutedToActiveLifecycle)
                    {
                        await _eventBus.PublishAsync(
                            new PlcAnomalyDetectedEvent { Anomaly = transition.Record },
                            cancellationToken);
                    }
                }
                else
                {
                    metrics.IncrementRecovered();
                    await _governanceStore.RecoverCandidateAsync(
                        transition.Record.AnomalyId,
                        transition.Record.EndTimeUtc ?? transition.Record.LastSeenUtc,
                        cancellationToken);
                    if (transition.RoutedToActiveLifecycle)
                    {
                        await _eventBus.PublishAsync(
                            new PlcAnomalyRecoveredEvent { Anomaly = transition.Record },
                            cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordFailure(ex);
        }
    }

    private static PeerTransition? ApplyLocked(
        PeerState state,
        PlcFeatureVector vector,
        string featureName,
        double actual,
        double median,
        double scaledMad,
        double deviation,
        bool abnormal,
        bool routed,
        int peerCount)
    {
        if (abnormal)
        {
            state.NormalCount = 0;
            state.AbnormalCount++;
            state.FirstAbnormalUtc ??= vector.WindowStartUtc;
            if (state.Active is null && state.AbnormalCount >= state.Profile.ConsecutivePeerAbnormalCount)
            {
                var record = CreateRecord(
                    state.Profile,
                    vector,
                    featureName,
                    actual,
                    median,
                    scaledMad,
                    deviation,
                    peerCount,
                    state.FirstAbnormalUtc.Value);
                state.Active = record;
                state.RoutedToActiveLifecycle = routed;
                return new PeerTransition(true, record, routed);
            }

            if (state.Active is not null)
            {
                state.Active = state.Active with
                {
                    Score = Math.Max(state.Active.Score, deviation),
                    ActualValue = actual,
                    ExpectedValue = median,
                    LowerBound = median - state.Profile.PeerMadMultiplier * scaledMad,
                    UpperBound = median + state.Profile.PeerMadMultiplier * scaledMad,
                    LastSeenUtc = vector.WindowEndUtc,
                    Reason = BuildReason(featureName, actual, median, scaledMad, deviation, peerCount, vector.ContextKey),
                    ContextJson = BuildContext(
                        vector,
                        featureName,
                        actual,
                        median,
                        scaledMad,
                        deviation,
                        peerCount)
                };
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
        if (state.NormalCount < state.Profile.ConsecutivePeerRecoveryCount) return null;
        var recovered = state.Active.Recover(vector.WindowEndUtc);
        var activeRoute = state.RoutedToActiveLifecycle;
        state.Active = null;
        state.RoutedToActiveLifecycle = false;
        state.NormalCount = 0;
        return new PeerTransition(false, recovered, activeRoute);
    }

    private static PlcAnomalyRecord CreateRecord(
        PlcMlProfile profile,
        PlcFeatureVector vector,
        string featureName,
        double actual,
        double median,
        double scaledMad,
        double deviation,
        int peerCount,
        DateTime startUtc)
    {
        var anomalyKey = $"PEER|{profile.ProfileId}|{vector.PlcName}|{vector.DeviceId}|{vector.ContextKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(anomalyKey));
        return new PlcAnomalyRecord
        {
            AnomalyId = Guid.NewGuid().ToString("N"),
            AnomalyKey = anomalyKey,
            AlarmCode = $"PLC_PEER_{Convert.ToHexString(hash.AsSpan(0, 8))}",
            RuleId = $"PEER:{profile.ProfileId}",
            Type = PlcAnomalyType.ContextualPeerComparison,
            Severity = profile.PeerSeverity,
            Status = PlcAnomalyLifecycleStatus.Active,
            PlcName = vector.PlcName,
            DbBlock = 0,
            DeviceId = vector.DeviceId,
            SignalName = featureName,
            DetectorName = "ContextualPeerMedianMad",
            ModelVersion = "peer-mad-v1",
            Score = deviation,
            ActualValue = actual,
            ExpectedValue = median,
            LowerBound = median - profile.PeerMadMultiplier * scaledMad,
            UpperBound = median + profile.PeerMadMultiplier * scaledMad,
            StartTimeUtc = startUtc,
            LastSeenUtc = vector.WindowEndUtc,
            Reason = BuildReason(featureName, actual, median, scaledMad, deviation, peerCount, vector.ContextKey),
            RaiseAlarm = profile.PeerRaiseAlarm,
            ContextJson = BuildContext(
                vector,
                featureName,
                actual,
                median,
                scaledMad,
                deviation,
                peerCount)
        };
    }

    private static string BuildReason(
        string featureName,
        double actual,
        double median,
        double scaledMad,
        double deviation,
        int peerCount,
        string contextKey) =>
        $"同群设备偏离：上下文 {contextKey}，特征 {featureName}={actual:G6}，同群中位数 {median:G6}，" +
        $"稳健尺度 {scaledMad:G6}，偏离 {deviation:F2}，同群设备数 {peerCount}。";

    private static string BuildContext(
        PlcFeatureVector vector,
        string featureName,
        double actual,
        double median,
        double scaledMad,
        double deviation,
        int peerCount) => PlcAnomalyRecord.SerializeContext(new
        {
            vector.ProfileId,
            vector.PlcName,
            vector.DeviceId,
            vector.ContextKey,
            vector.WindowStartUtc,
            vector.WindowEndUtc,
            featureName,
            actual,
            median,
            scaledMad,
            deviation,
            peerCount
        });

    private static PlcMlCandidateRecord ToCandidate(
        PlcMlProfile profile,
        PlcAnomalyRecord record,
        bool routed) => new()
    {
        CandidateId = record.AnomalyId,
        CandidateKey = record.AnomalyKey,
        ProfileId = profile.ProfileId,
        ModelVersion = record.ModelVersion,
        DeploymentMode = profile.DeploymentMode,
        RoutedToActiveLifecycle = routed,
        PlcName = record.PlcName,
        DeviceId = record.DeviceId,
        WindowStartUtc = record.StartTimeUtc,
        WindowEndUtc = record.LastSeenUtc,
        Score = record.Score,
        Threshold = profile.PeerMadMultiplier,
        Explanation = record.Reason,
        ContextJson = record.ContextJson,
        IsActive = true,
        DetectedUtc = record.StartTimeUtc,
        ReviewDecision = PlcMlReviewDecision.Unreviewed
    };

    private static bool ShouldRouteToActiveLifecycle(PlcMlProfile profile, string deviceId)
    {
        if (profile.DeploymentMode == PlcMlDeploymentMode.Active) return true;
        if (profile.DeploymentMode != PlcMlDeploymentMode.Canary || profile.CanaryPercentage <= 0) return false;
        if (profile.CanaryPercentage >= 100) return true;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{profile.ProfileId}|{deviceId}"));
        var bucket = ((hash[0] << 8) | hash[1]) % 100;
        return bucket < profile.CanaryPercentage;
    }

    private static double Median(double[] sorted)
    {
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static string BuildBucketKey(PlcFeatureVector vector) =>
        $"{vector.ProfileId}|{vector.ContextKey}|{vector.WindowStartUtc.Ticks}";

    private sealed class PeerBucket
    {
        public PeerBucket(
            PlcMlProfile profile,
            string contextKey,
            DateTime windowStartUtc,
            DateTime windowEndUtc)
        {
            Profile = profile;
            ContextKey = contextKey;
            WindowStartUtc = windowStartUtc;
            WindowEndUtc = windowEndUtc;
        }

        public object Gate { get; } = new();
        public PlcMlProfile Profile { get; }
        public string ContextKey { get; }
        public DateTime WindowStartUtc { get; }
        public DateTime WindowEndUtc { get; }
        public string[]? FeatureNames { get; set; }
        public Dictionary<string, PlcFeatureVector> Vectors { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PeerState
    {
        public PeerState(PlcMlProfile profile) => Profile = profile;
        public object Gate { get; } = new();
        public PlcMlProfile Profile { get; }
        public int AbnormalCount { get; set; }
        public int NormalCount { get; set; }
        public DateTime? FirstAbnormalUtc { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public PlcAnomalyRecord? Active { get; set; }
        public bool RoutedToActiveLifecycle { get; set; }
    }

    private sealed class PeerMetrics
    {
        private long _bucketsEvaluated;
        private long _devicesEvaluated;
        private long _raised;
        private long _recovered;
        private long _shadowRaised;
        private long _activeRaised;
        private long _skippedBuckets;
        private long _failures;

        public long BucketsEvaluated => Interlocked.Read(ref _bucketsEvaluated);
        public long DevicesEvaluated => Interlocked.Read(ref _devicesEvaluated);
        public long Raised => Interlocked.Read(ref _raised);
        public long Recovered => Interlocked.Read(ref _recovered);
        public long ShadowRaised => Interlocked.Read(ref _shadowRaised);
        public long ActiveRaised => Interlocked.Read(ref _activeRaised);
        public long SkippedBuckets => Interlocked.Read(ref _skippedBuckets);
        public long Failures => Interlocked.Read(ref _failures);

        public void IncrementBucket(int devices)
        {
            Interlocked.Increment(ref _bucketsEvaluated);
            Interlocked.Add(ref _devicesEvaluated, devices);
        }
        public void IncrementRaised(bool active)
        {
            Interlocked.Increment(ref _raised);
            if (active) Interlocked.Increment(ref _activeRaised);
            else Interlocked.Increment(ref _shadowRaised);
        }
        public void IncrementRecovered() => Interlocked.Increment(ref _recovered);
        public void IncrementSkipped() => Interlocked.Increment(ref _skippedBuckets);
        public void RecordFailure(Exception _) => Interlocked.Increment(ref _failures);
    }

    private sealed record PeerTransition(
        bool IsDetected,
        PlcAnomalyRecord Record,
        bool RoutedToActiveLifecycle);
}
