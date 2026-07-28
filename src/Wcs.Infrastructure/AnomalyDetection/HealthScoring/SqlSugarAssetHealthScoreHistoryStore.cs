namespace Wcs.Infrastructure.AnomalyDetection.HealthScoring;

using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;

[SugarTable("Wcs_AssetHealthScore")]
public sealed class AssetHealthScoreEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 64)]
    public string PointId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string AssetId { get; set; } = string.Empty;

    public double HealthScore { get; set; }
    public double PreviousHealthScore { get; set; }
    public double ScoreDelta { get; set; }
    public int Grade { get; set; }
    public int PreviousGrade { get; set; }
    public bool GradeChanged { get; set; }
    public int Direction { get; set; }
    public double FusionRiskScore { get; set; }
    public int FusionStatus { get; set; }
    public int IndependentSourceCount { get; set; }
    public DateTime CalculatedAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }

    [SugarColumn(Length = 2000)]
    public string Summary { get; set; } = string.Empty;
}

/// <summary>
/// SQL Server 健康历史 Provider。写入通过有界 Channel 与控制路径隔离，
/// 每批使用独立 SqlSugarClient，失败批次保留并重试，队列满时显式计数而不阻塞调用方。
/// </summary>
public sealed class SqlSugarAssetHealthScoreHistoryStore : BackgroundService, IAssetHealthScoreHistoryStore
{
    private readonly string _connectionString;
    private readonly AssetHealthScoringOptions _options;
    private readonly ILogger<SqlSugarAssetHealthScoreHistoryStore> _logger;
    private readonly Channel<PendingPoint> _channel;
    private readonly object _lastPointGate = new();
    private readonly Dictionary<string, AssetHealthScorePoint> _lastAcceptedPoints = new(StringComparer.Ordinal);
    private readonly object _statusGate = new();

    private long _recordedPoints;
    private long _persistedPoints;
    private long _deduplicatedPoints;
    private long _idempotentDuplicatePoints;
    private long _droppedWrites;
    private long _failedWriteBatches;
    private long _pendingWrites;
    private long _evictedPoints;
    private long _evictedAssets;
    private DateTime? _lastSuccessfulWriteUtc;
    private DateTime _nextMaintenanceUtc = DateTime.MinValue;
    private string? _lastError;
    private bool _schemaReady;

    public SqlSugarAssetHealthScoreHistoryStore(
        string connectionString,
        AssetHealthScoringOptions options,
        ILogger<SqlSugarAssetHealthScoreHistoryStore> logger)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("WcsDb connection string is required.", nameof(connectionString))
            : connectionString;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = Channel.CreateBounded<PendingPoint>(new BoundedChannelOptions(options.HistoryWriteChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public string Provider => "SqlServer";

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
        AssetHealthScorePoint point;

        lock (_lastPointGate)
        {
            _lastAcceptedPoints.TryGetValue(assetId, out var previous);
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
                Interlocked.Increment(ref _deduplicatedPoints);
                return ValueTask.FromResult(false);
            }

            point = CreatePoint(assetId, snapshot, previous, timestamp);
            var pending = new PendingPoint(CreatePointId(point), point);
            if (!_channel.Writer.TryWrite(pending))
            {
                Interlocked.Increment(ref _droppedWrites);
                SetLastError("Health history SQL write channel is full; point was rejected.");
                return ValueTask.FromResult(false);
            }

            _lastAcceptedPoints[assetId] = point;
        }

        Interlocked.Increment(ref _recordedPoints);
        Interlocked.Increment(ref _pendingWrites);
        return ValueTask.FromResult(true);
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

        using var db = CreateClient();
        var query = db.Queryable<AssetHealthScoreEntity>()
            .Where(row => row.AssetId == normalizedAssetId);
        if (fromUtc is not null)
            query = query.Where(row => row.RecordedAtUtc >= fromUtc.Value);
        if (toUtc is not null)
            query = query.Where(row => row.RecordedAtUtc <= toUtc.Value);

        var newestFirst = query
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Skip(skip)
            .Take(maximumCount + 1)
            .ToList();
        var hasMore = newestFirst.Count > maximumCount;
        var items = newestFirst
            .Take(maximumCount)
            .Select(ToPoint)
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

        var normalizedAssetId = assetId.Trim();
        var effectiveWindow = Math.Clamp(
            windowSize ?? _options.TrendWindowSize,
            2,
            _options.MaximumHistoryQueryCount);
        using var db = CreateClient();
        var query = db.Queryable<AssetHealthScoreEntity>()
            .Where(row => row.AssetId == normalizedAssetId);
        if (fromUtc is not null)
            query = query.Where(row => row.RecordedAtUtc >= fromUtc.Value);
        if (toUtc is not null)
            query = query.Where(row => row.RecordedAtUtc <= toUtc.Value);

        var points = query
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(effectiveWindow)
            .ToList()
            .Select(ToPoint)
            .Reverse()
            .ToArray();
        return ValueTask.FromResult(CalculateTrend(normalizedAssetId, points));
    }

    public ValueTask<AssetHealthHistoryStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = false;
        var retained = 0;
        var trackedAssets = 0;
        try
        {
            using var db = CreateClient();
            retained = db.Queryable<AssetHealthScoreEntity>().Count();
            trackedAssets = db.Queryable<AssetHealthScoreEntity>()
                .Select(row => row.AssetId)
                .Distinct()
                .ToList()
                .Count;
            available = true;
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
        }

        DateTime? lastSuccessful;
        string? lastError;
        lock (_statusGate)
        {
            lastSuccessful = _lastSuccessfulWriteUtc;
            lastError = _lastError;
        }

        return ValueTask.FromResult(new AssetHealthHistoryStoreStatus
        {
            Enabled = _options.Enabled,
            Provider = Provider,
            IsAvailable = available,
            TrackedAssets = trackedAssets,
            RetainedPoints = retained,
            RecordedPoints = Interlocked.Read(ref _recordedPoints),
            PersistedPoints = Interlocked.Read(ref _persistedPoints),
            DeduplicatedPoints = Interlocked.Read(ref _deduplicatedPoints),
            IdempotentDuplicatePoints = Interlocked.Read(ref _idempotentDuplicatePoints),
            DroppedWrites = Interlocked.Read(ref _droppedWrites),
            FailedWriteBatches = Interlocked.Read(ref _failedWriteBatches),
            PendingWrites = Interlocked.Read(ref _pendingWrites),
            EvictedPoints = Interlocked.Read(ref _evictedPoints),
            EvictedAssets = Interlocked.Read(ref _evictedAssets),
            MaximumHistoryPerAsset = _options.MaximumHistoryPerAsset,
            MaximumTrackedHistoryAssets = _options.MaximumTrackedHistoryAssets,
            HistoryRetentionHours = _options.HistoryRetentionHours,
            SamplingIntervalSeconds = _options.SamplingIntervalSeconds,
            LastSuccessfulWriteUtc = lastSuccessful,
            LastError = lastError
        });
    }

    public ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || utcNow < _nextMaintenanceUtc)
            return ValueTask.CompletedTask;

        _nextMaintenanceUtc = utcNow.AddSeconds(_options.HistoryMaintenanceIntervalSeconds);
        try
        {
            using var db = CreateClient();
            var parameters = new[]
            {
                new SugarParameter("@BatchSize", _options.HistoryMaintenanceBatchSize),
                new SugarParameter("@CutoffUtc", utcNow.AddHours(-_options.HistoryRetentionHours)),
                new SugarParameter("@MaximumPerAsset", _options.MaximumHistoryPerAsset),
                new SugarParameter("@MaximumAssets", _options.MaximumTrackedHistoryAssets)
            };

            var retentionDeleted = db.Ado.ExecuteCommand(@"
DELETE TOP (@BatchSize)
FROM Wcs_AssetHealthScore
WHERE RecordedAtUtc < @CutoffUtc;", parameters);

            var perAssetDeleted = db.Ado.ExecuteCommand(@"
;WITH RankedPoints AS
(
    SELECT Sequence,
           ROW_NUMBER() OVER (PARTITION BY AssetId ORDER BY RecordedAtUtc DESC, Sequence DESC) AS RowNumber
    FROM Wcs_AssetHealthScore
)
DELETE TOP (@BatchSize) H
FROM Wcs_AssetHealthScore H
INNER JOIN RankedPoints R ON R.Sequence = H.Sequence
WHERE R.RowNumber > @MaximumPerAsset;", parameters);

            var assetDeleted = db.Ado.ExecuteCommand(@"
;WITH RankedAssets AS
(
    SELECT AssetId,
           ROW_NUMBER() OVER (ORDER BY MAX(RecordedAtUtc) DESC, AssetId ASC) AS RowNumber
    FROM Wcs_AssetHealthScore
    GROUP BY AssetId
)
DELETE TOP (@BatchSize) H
FROM Wcs_AssetHealthScore H
INNER JOIN RankedAssets A ON A.AssetId = H.AssetId
WHERE A.RowNumber > @MaximumAssets;", parameters);

            Interlocked.Add(ref _evictedPoints, retentionDeleted + perAssetDeleted + assetDeleted);
            if (assetDeleted > 0) Interlocked.Increment(ref _evictedAssets);
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
            _nextMaintenanceUtc = utcNow.AddMilliseconds(_options.HistoryWriteRetryDelayMs);
            _logger.LogWarning(exception, "Asset health SQL history maintenance failed.");
        }

        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureSchemaWithRetryAsync(stoppingToken);
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<PendingPoint>(_options.HistoryWriteBatchSize);
            while (batch.Count < _options.HistoryWriteBatchSize && reader.TryRead(out var point))
                batch.Add(point);
            if (batch.Count == 0) continue;
            await PersistWithRetryAsync(batch, stoppingToken);
        }
    }

    private async Task EnsureSchemaWithRetryAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                EnsureSchema();
                return;
            }
            catch (Exception exception)
            {
                SetLastError(exception.Message);
                _logger.LogWarning(exception, "Asset health SQL schema initialization failed; retrying.");
                await Task.Delay(_options.HistoryWriteRetryDelayMs, cancellationToken);
            }
        }
    }

    private async Task PersistWithRetryAsync(
        IReadOnlyList<PendingPoint> batch,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PersistBatch(batch);
                Interlocked.Add(ref _pendingWrites, -batch.Count);
                return;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedWriteBatches);
                SetLastError(exception.Message);
                _logger.LogWarning(
                    exception,
                    "Asset health SQL batch failed. BatchSize={BatchSize}, Pending={Pending}",
                    batch.Count,
                    Interlocked.Read(ref _pendingWrites));
                await Task.Delay(_options.HistoryWriteRetryDelayMs, cancellationToken);
            }
        }
    }

    private void PersistBatch(IReadOnlyList<PendingPoint> batch)
    {
        using var db = CreateClient();
        var pointIds = batch.Select(static item => item.PointId).Distinct(StringComparer.Ordinal).ToArray();
        var existing = db.Queryable<AssetHealthScoreEntity>()
            .Where(row => pointIds.Contains(row.PointId))
            .Select(row => row.PointId)
            .ToList()
            .ToHashSet(StringComparer.Ordinal);
        var insert = batch
            .Where(item => !existing.Contains(item.PointId))
            .Select(ToEntity)
            .ToArray();

        if (insert.Length > 0)
            db.Insertable(insert).ExecuteCommand();

        Interlocked.Add(ref _persistedPoints, insert.Length);
        Interlocked.Add(ref _idempotentDuplicatePoints, batch.Count - insert.Length);
        lock (_statusGate)
        {
            _lastSuccessfulWriteUtc = DateTime.UtcNow;
            _lastError = null;
        }
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        using var db = CreateClient();
        db.CodeFirst.InitTables<AssetHealthScoreEntity>();
        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AssetHealthScore_PointId' AND object_id = OBJECT_ID('Wcs_AssetHealthScore'))
    CREATE UNIQUE INDEX UX_Wcs_AssetHealthScore_PointId ON Wcs_AssetHealthScore(PointId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AssetHealthScore_AssetTime' AND object_id = OBJECT_ID('Wcs_AssetHealthScore'))
    CREATE INDEX IX_Wcs_AssetHealthScore_AssetTime ON Wcs_AssetHealthScore(AssetId, RecordedAtUtc DESC, Sequence DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AssetHealthScore_Time' AND object_id = OBJECT_ID('Wcs_AssetHealthScore'))
    CREATE INDEX IX_Wcs_AssetHealthScore_Time ON Wcs_AssetHealthScore(RecordedAtUtc);");
        _schemaReady = true;
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private AssetHealthScorePoint CreatePoint(
        string assetId,
        AssetHealthScoreSnapshot snapshot,
        AssetHealthScorePoint? previous,
        DateTime timestamp)
    {
        var delta = previous is null ? 0 : snapshot.HealthScore - previous.HealthScore;
        return new AssetHealthScorePoint
        {
            Sequence = 0,
            AssetId = assetId,
            HealthScore = snapshot.HealthScore,
            PreviousHealthScore = previous?.HealthScore ?? snapshot.HealthScore,
            ScoreDelta = Math.Round(delta, 2, MidpointRounding.AwayFromZero),
            Grade = snapshot.Grade,
            PreviousGrade = previous?.Grade ?? snapshot.Grade,
            GradeChanged = previous is not null && previous.Grade != snapshot.Grade,
            Direction = ResolveDirection(delta, _options.MinimumScoreChangeToRecord),
            FusionRiskScore = snapshot.FusionRiskScore,
            FusionStatus = snapshot.FusionStatus,
            IndependentSourceCount = snapshot.IndependentSourceCount,
            CalculatedAtUtc = snapshot.CalculatedAtUtc,
            RecordedAtUtc = timestamp,
            Summary = snapshot.Summary
        };
    }

    private AssetHealthTrendSnapshot? CalculateTrend(
        string assetId,
        IReadOnlyList<AssetHealthScorePoint> points)
    {
        if (points.Count == 0) return null;
        var first = points[0];
        var last = points[^1];
        var delta = last.HealthScore - first.HealthScore;
        var durationHours = (last.RecordedAtUtc - first.RecordedAtUtc).TotalHours;
        return new AssetHealthTrendSnapshot
        {
            AssetId = assetId,
            Direction = ResolveDirection(delta, _options.TrendChangeThreshold),
            CurrentHealthScore = last.HealthScore,
            ScoreDelta = Math.Round(delta, 2, MidpointRounding.AwayFromZero),
            AverageHealthScore = Math.Round(points.Average(static point => point.HealthScore), 2, MidpointRounding.AwayFromZero),
            MinimumHealthScore = points.Min(static point => point.HealthScore),
            MaximumHealthScore = points.Max(static point => point.HealthScore),
            HealthScoreSlopePerHour = Math.Round(durationHours <= 0 ? 0 : delta / durationHours, 2, MidpointRounding.AwayFromZero),
            SampleCount = points.Count,
            CurrentGrade = last.Grade,
            WindowStartUtc = first.RecordedAtUtc,
            WindowEndUtc = last.RecordedAtUtc
        };
    }

    private static AssetHealthTrendDirection ResolveDirection(double delta, double threshold)
    {
        if (delta >= threshold) return AssetHealthTrendDirection.Improving;
        if (delta <= -threshold) return AssetHealthTrendDirection.Deteriorating;
        return AssetHealthTrendDirection.Stable;
    }

    private static string CreatePointId(AssetHealthScorePoint point)
    {
        var raw = string.Join('|',
            point.AssetId,
            point.RecordedAtUtc.ToUniversalTime().Ticks,
            point.CalculatedAtUtc.ToUniversalTime().Ticks,
            point.HealthScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            (int)point.Grade);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static AssetHealthScoreEntity ToEntity(PendingPoint pending) => new()
    {
        PointId = pending.PointId,
        AssetId = pending.Point.AssetId,
        HealthScore = pending.Point.HealthScore,
        PreviousHealthScore = pending.Point.PreviousHealthScore,
        ScoreDelta = pending.Point.ScoreDelta,
        Grade = (int)pending.Point.Grade,
        PreviousGrade = (int)pending.Point.PreviousGrade,
        GradeChanged = pending.Point.GradeChanged,
        Direction = (int)pending.Point.Direction,
        FusionRiskScore = pending.Point.FusionRiskScore,
        FusionStatus = (int)pending.Point.FusionStatus,
        IndependentSourceCount = pending.Point.IndependentSourceCount,
        CalculatedAtUtc = pending.Point.CalculatedAtUtc,
        RecordedAtUtc = pending.Point.RecordedAtUtc,
        Summary = pending.Point.Summary
    };

    private static AssetHealthScorePoint ToPoint(AssetHealthScoreEntity entity) => new()
    {
        Sequence = entity.Sequence,
        AssetId = entity.AssetId,
        HealthScore = entity.HealthScore,
        PreviousHealthScore = entity.PreviousHealthScore,
        ScoreDelta = entity.ScoreDelta,
        Grade = (AssetHealthGrade)entity.Grade,
        PreviousGrade = (AssetHealthGrade)entity.PreviousGrade,
        GradeChanged = entity.GradeChanged,
        Direction = (AssetHealthTrendDirection)entity.Direction,
        FusionRiskScore = entity.FusionRiskScore,
        FusionStatus = (FusedHealthStatus)entity.FusionStatus,
        IndependentSourceCount = entity.IndependentSourceCount,
        CalculatedAtUtc = entity.CalculatedAtUtc,
        RecordedAtUtc = entity.RecordedAtUtc,
        Summary = entity.Summary
    };

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

    private void SetLastError(string error)
    {
        lock (_statusGate)
            _lastError = error;
    }

    private sealed record PendingPoint(string PointId, AssetHealthScorePoint Point);
}