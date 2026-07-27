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
    private DateTime? _lastSuccessfulWriteUtc;

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

            var point = CreatePoint(++_sequence, assetId, snapshot, previous, timestamp, _options);
            state.Points.AddLast(point);
            state.LastRecordedAtUtc = timestamp;
            _recordedPoints++;
            _lastSuccessfulWriteUtc = timestamp;
            TrimAssetCapacityLocked(state);
            return ValueTask.FromResult(true);
        }
    }

    public async ValueTask<IReadOnlyList<AssetHealthScorePoint>> GetHistoryAsync(
        string assetId,
        DateTime? fromUtc = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        var page = await GetHistoryPageAsync(
            assetId,
            fromUtc,
            toUtc: null,
            skip: 0,
            maximumCount,
            cancellationToken);
        return page.Items;
    }

    public ValueTask<AssetHealthHistoryPage> GetHistoryPageAsync(
        string assetId,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int skip = 0,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedAssetId = assetId?.Trim() ?? string.Empty;
        skip = Math.Max(0, skip);
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumHistoryQueryCount);

        if (!_options.Enabled || normalizedAssetId.Length == 0)
            return ValueTask.FromResult(EmptyPage(normalizedAssetId, fromUtc, toUtc, skip));

        lock (_gate)
        {
            if (!_assets.TryGetValue(normalizedAssetId, out var state))
                return ValueTask.FromResult(EmptyPage(normalizedAssetId, fromUtc, toUtc, skip));

            var newestFirst = state.Points
                .Where(point => fromUtc is null || point.RecordedAtUtc >= fromUtc.Value)
                .Where(point => toUtc is null || point.RecordedAtUtc <= toUtc.Value)
                .Reverse()
                .Skip(skip)
                .Take(maximumCount + 1)
                .ToArray();
            var hasMore = newestFirst.Length > maximumCount;
            var items = newestFirst
                .Take(maximumCount)
                .Reverse()
                .ToArray();

            return ValueTask.FromResult(new AssetHealthHistoryPage
            {
                AssetId = normalizedAssetId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Skip = skip,
                Count = items.Length,
                HasMore = hasMore,
                Items = items
            });
        }
    }

    public ValueTask<AssetHealthTrendSnapshot?> GetTrendAsync(
        string assetId,
        int? windowSize = null,
        CancellationToken cancellationToken = default) =>
        GetTrendRangeAsync(assetId, fromUtc: null, toUtc: null, windowSize, cancellationToken);

    public ValueTask<AssetHealthTrendSnapshot?> GetTrendRangeAsync(
        string assetId,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
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

            var points = state.Points
                .Where(point => fromUtc is null || point.RecordedAtUtc >= fromUtc.Value)
                .Where(point => toUtc is null || point.RecordedAtUtc <= toUtc.Value)
                .TakeLast(effectiveWindow)
                .ToArray();
            return ValueTask.FromResult(CalculateTrend(state.AssetId, points, _options.TrendChangeThreshold));
        }
    }

    public ValueTask<AssetHealthHistoryStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var retained = _assets.Values.Sum(static state => state.Points.Count);
            return ValueTask.FromResult(new AssetHealthHistoryStoreStatus
            {
                Enabled = _options.Enabled,
                Provider = Provider,
                IsAvailable = true,
                TrackedAssets = _assets.Count,
                RetainedPoints = retained,
                RecordedPoints = _recordedPoints,
                PersistedPoints = retained,
                DeduplicatedPoints = _deduplicatedPoints,
                IdempotentDuplicatePoints = 0,
                DroppedWrites = 0,
                FailedWriteBatches = 0,
                PendingWrites = 0,
                EvictedPoints = _evictedPoints,
                EvictedAssets = _evictedAssets,
                MaximumHistoryPerAsset = _options.MaximumHistoryPerAsset,
                MaximumTrackedHistoryAssets = _options.MaximumTrackedHistoryAssets,
                HistoryRetentionHours = _options.HistoryRetentionHours,
                SamplingIntervalSeconds = _options.SamplingIntervalSeconds,
                LastSuccessfulWriteUtc = _lastSuccessfulWriteUtc,
                LastError = null
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

    internal static AssetHealthScorePoint CreatePoint(
        long sequence,
        string assetId,
        AssetHealthScoreSnapshot snapshot,
        AssetHealthScorePoint? previous,
        DateTime timestamp,
        AssetHealthScoringOptions options)
    {
        var delta = previous is null ? 0 : snapshot.HealthScore - previous.HealthScore;
        var gradeChanged = previous is not null && previous.Grade != snapshot.Grade;
        return new AssetHealthScorePoint
        {
            Sequence = sequence,
            AssetId = assetId,
            HealthScore = snapshot.HealthScore,
            PreviousHealthScore = previous?.HealthScore ?? snapshot.HealthScore,
            ScoreDelta = Math.Round(delta, 2, MidpointRounding.AwayFromZero),
            Grade = snapshot.Grade,
            PreviousGrade = previous?.Grade ?? snapshot.Grade,
            GradeChanged = gradeChanged,
            Direction = ResolveDirection(delta, options.MinimumScoreChangeToRecord),
            FusionRiskScore = snapshot.FusionRiskScore,
            FusionStatus = snapshot.FusionStatus,
            IndependentSourceCount = snapshot.IndependentSourceCount,
            CalculatedAtUtc = snapshot.CalculatedAtUtc,
            RecordedAtUtc = timestamp,
            Summary = snapshot.Summary
        };
    }

    internal static AssetHealthTrendSnapshot? CalculateTrend(
        string assetId,
        IReadOnlyList<AssetHealthScorePoint> points,
        double changeThreshold)
    {
        if (points.Count == 0) return null;
        var first = points[0];
        var last = points[^1];
        var delta = last.HealthScore - first.HealthScore;
        var durationHours = (last.RecordedAtUtc - first.RecordedAtUtc).TotalHours;
        var slope = durationHours <= 0 ? 0 : delta / durationHours;
        return new AssetHealthTrendSnapshot
        {
            AssetId = assetId,
            Direction = ResolveDirection(delta, changeThreshold),
            CurrentHealthScore = last.HealthScore,
            ScoreDelta = Math.Round(delta, 2, MidpointRounding.AwayFromZero),
            AverageHealthScore = Math.Round(points.Average(static point => point.HealthScore), 2, MidpointRounding.AwayFromZero),
            MinimumHealthScore = points.Min(static point => point.HealthScore),
            MaximumHealthScore = points.Max(static point => point.HealthScore),
            HealthScoreSlopePerHour = Math.Round(slope, 2, MidpointRounding.AwayFromZero),
            SampleCount = points.Count,
            CurrentGrade = last.Grade,
            WindowStartUtc = first.RecordedAtUtc,
            WindowEndUtc = last.RecordedAtUtc
        };
    }

    internal static AssetHealthTrendDirection ResolveDirection(double delta, double threshold)
    {
        if (delta >= threshold) return AssetHealthTrendDirection.Improving;
        if (delta <= -threshold) return AssetHealthTrendDirection.Deteriorating;
        return AssetHealthTrendDirection.Stable;
    }

    private static AssetHealthHistoryPage EmptyPage(
        string assetId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int skip) => new()
    {
        AssetId = assetId,
        FromUtc = fromUtc,
        ToUtc = toUtc,
        Skip = skip,
        Count = 0,
        HasMore = false,
        Items = Array.Empty<AssetHealthScorePoint>()
    };

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

    private sealed class AssetHistoryState
    {
        public AssetHistoryState(string assetId) => AssetId = assetId;
        public string AssetId { get; }
        public LinkedList<AssetHealthScorePoint> Points { get; } = new();
        public DateTime LastRecordedAtUtc { get; set; }
    }
}