namespace Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 有界、线程安全的健康分历史仓储。仅保存变化点和周期心跳点，避免固定采样造成无界增长。
/// </summary>
public sealed class InMemoryAssetHealthScoreHistoryStore : IAssetHealthScoreHistoryStore
{
    private readonly AssetHealthScoringOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, AssetHistoryState> _assets = new(StringComparer.Ordinal);
    private long _sequence;
    private long _recordedPoints;
    private long _deduplicatedPoints;
    private long _evictedPoints;
    private long _evictedAssets;

    public InMemoryAssetHealthScoreHistoryStore(AssetHealthScoringOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public string Provider => "Memory";

    public ValueTask<bool> RecordAsync(
        AssetHealthScoreSnapshot snapshot,
        DateTime recordedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(snapshot.AssetId))
            return ValueTask.FromResult(false);

        var assetId = snapshot.AssetId.Trim();
        var timestamp = recordedAtUtc == default ? DateTime.UtcNow : recordedAtUtc;

        lock (_gate)
        {
            if (!_assets.TryGetValue(assetId, out var state))
            {
                EnsureAssetCapacityLocked();
                state = new AssetHistoryState(assetId);
                _assets.Add(assetId, state);
            }

            var previous = state.Points.Last?.Value;
            var delta = previous is null ? 0 : snapshot.HealthScore - previous.HealthScore;
            var gradeChanged = previous is not null && previous.Grade != snapshot.Grade;
            var timeSinceLast = previous is null
                ? TimeSpan.MaxValue
                : timestamp - previous.RecordedAtUtc;
            var shouldRecord = previous is null ||
                gradeChanged ||
                Math.Abs(delta) >= _options.MinimumScoreChangeToRecord ||
                timeSinceLast >= TimeSpan.FromSeconds(_options.MaximumUnchangedIntervalSeconds);

            if (!shouldRecord)
            {
                _deduplicatedPoints++;
                return ValueTask.FromResult(false);
            }

            var point = new AssetHealthScorePoint
            {
                Sequence = ++_sequence,
                AssetId = assetId,
                HealthScore = snapshot.HealthScore,
                PreviousHealthScore = previous?.HealthScore ?? snapshot.HealthScore,
                ScoreDelta = Math.Round(delta, 2, MidpointRounding.AwayFromZero),
                Grade = snapshot.Grade,
                PreviousGrade = previous?.Grade ?? snapshot.Grade,
                GradeChanged = gradeChanged,
                Direction = ResolveDirection(delta, _options.MinimumScoreChangeToRecord),
                FusionRiskScore = snapshot.FusionRiskScore,
                FusionStatus = snapshot.FusionStatus,
                IndependentSourceCount = snapshot.IndependentSourceCount,
                CalculatedAtUtc = snapshot.CalculatedAtUtc,
                RecordedAtUtc = timestamp,
                Summary = snapshot.Summary
            };

            state.Points.AddLast(point);
            state.LastRecordedAtUtc = timestamp;
            _recordedPoints++;
            TrimAssetCapacityLocked(state);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<IReadOnlyList<AssetHealthScorePoint>> GetHistoryAsync(
        string assetId,
        DateTime? fromUtc = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(assetId))
            return ValueTask.FromResult<IReadOnlyList<AssetHealthScorePoint>>(
                Array.Empty<AssetHealthScorePoint>());

        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumHistoryQueryCount);
        lock (_gate)
        {
            if (!_assets.TryGetValue(assetId.Trim(), out var state))
                return ValueTask.FromResult<IReadOnlyList<AssetHealthScorePoint>>(
                    Array.Empty<AssetHealthScorePoint>());

            var result = state.Points
                .Where(point => fromUtc is null || point.RecordedAtUtc >= fromUtc.Value)
                .TakeLast(maximumCount)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<AssetHealthScorePoint>>(result);
        }
    }

    public ValueTask<AssetHealthTrendSnapshot?> GetTrendAsync(
        string assetId,
        int? windowSize = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || string.IsNullOrWhiteSpace(assetId))
            return ValueTask.FromResult<AssetHealthTrendSnapshot?>(null);

        var effectiveWindow = Math.Clamp(
            windowSize ?? _options.TrendWindowSize,
            2,
            _options.MaximumHistoryPerAsset);

        lock (_gate)
        {
            if (!_assets.TryGetValue(assetId.Trim(), out var state) || state.Points.Count == 0)
                return ValueTask.FromResult<AssetHealthTrendSnapshot?>(null);

            var points = state.Points.TakeLast(effectiveWindow).ToArray();
            var first = points[0];
            var last = points[^1];
            var delta = last.HealthScore - first.HealthScore;
            var durationHours = (last.RecordedAtUtc - first.RecordedAtUtc).TotalHours;
            var slope = durationHours <= 0 ? 0 : delta / durationHours;
            var direction = ResolveDirection(delta, _options.TrendChangeThreshold);

            var trend = new AssetHealthTrendSnapshot
            {
                AssetId = state.AssetId,
                Direction = direction,
                CurrentHealthScore = last.HealthScore,
                ScoreDelta = Math.Round(delta, 2, MidpointRounding.AwayFromZero),
                AverageHealthScore = Math.Round(
                    points.Average(static point => point.HealthScore),
                    2,
                    MidpointRounding.AwayFromZero),
                MinimumHealthScore = points.Min(static point => point.HealthScore),
                MaximumHealthScore = points.Max(static point => point.HealthScore),
                HealthScoreSlopePerHour = Math.Round(slope, 2, MidpointRounding.AwayFromZero),
                SampleCount = points.Length,
                CurrentGrade = last.Grade,
                WindowStartUtc = first.RecordedAtUtc,
                WindowEndUtc = last.RecordedAtUtc
            };
            return ValueTask.FromResult<AssetHealthTrendSnapshot?>(trend);
        }
    }

    public ValueTask<AssetHealthHistoryStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(new AssetHealthHistoryStoreStatus
            {
                Enabled = _options.Enabled,
                Provider = Provider,
                TrackedAssets = _assets.Count,
                RetainedPoints = _assets.Values.Sum(static state => state.Points.Count),
                RecordedPoints = _recordedPoints,
                DeduplicatedPoints = _deduplicatedPoints,
                EvictedPoints = _evictedPoints,
                EvictedAssets = _evictedAssets,
                MaximumHistoryPerAsset = _options.MaximumHistoryPerAsset,
                MaximumTrackedHistoryAssets = _options.MaximumTrackedHistoryAssets,
                HistoryRetentionHours = _options.HistoryRetentionHours,
                SamplingIntervalSeconds = _options.SamplingIntervalSeconds
            });
        }
    }

    public ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled) return ValueTask.CompletedTask;

        var cutoff = utcNow.AddHours(-_options.HistoryRetentionHours);
        lock (_gate)
        {
            foreach (var pair in _assets.ToArray())
            {
                var state = pair.Value;
                while (state.Points.First is { } first && first.Value.RecordedAtUtc < cutoff)
                {
                    state.Points.RemoveFirst();
                    _evictedPoints++;
                }

                if (state.Points.Count != 0) continue;
                _assets.Remove(pair.Key);
                _evictedAssets++;
            }
        }

        return ValueTask.CompletedTask;
    }

    private void EnsureAssetCapacityLocked()
    {
        if (_assets.Count < _options.MaximumTrackedHistoryAssets) return;

        var oldest = _assets.Values
            .OrderBy(static state => state.LastRecordedAtUtc)
            .ThenBy(static state => state.AssetId, StringComparer.Ordinal)
            .First();
        _assets.Remove(oldest.AssetId);
        _evictedPoints += oldest.Points.Count;
        _evictedAssets++;
    }

    private void TrimAssetCapacityLocked(AssetHistoryState state)
    {
        while (state.Points.Count > _options.MaximumHistoryPerAsset)
        {
            state.Points.RemoveFirst();
            _evictedPoints++;
        }
    }

    private static AssetHealthTrendDirection ResolveDirection(double delta, double threshold)
    {
        if (delta >= threshold) return AssetHealthTrendDirection.Improving;
        if (delta <= -threshold) return AssetHealthTrendDirection.Deteriorating;
        return AssetHealthTrendDirection.Stable;
    }

    private sealed class AssetHistoryState
    {
        public AssetHistoryState(string assetId) => AssetId = assetId;
        public string AssetId { get; }
        public LinkedList<AssetHealthScorePoint> Points { get; } = new();
        public DateTime LastRecordedAtUtc { get; set; }
    }
}
