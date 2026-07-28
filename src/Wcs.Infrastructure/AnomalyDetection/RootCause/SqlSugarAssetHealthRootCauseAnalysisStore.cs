namespace Wcs.Infrastructure.AnomalyDetection.RootCause;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Wcs.Core.AnomalyDetection.RootCause;

[SugarTable("Wcs_AssetHealthRootCauseGraphVersion")]
public sealed class AssetHealthRootCauseGraphVersionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 128)]
    public string Version { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string GraphHash { get; set; } = string.Empty;

    [SugarColumn(Length = 256)]
    public string Source { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string ApprovedBy { get; set; } = string.Empty;

    public DateTime ApprovedAtUtc { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string GraphJson { get; set; } = string.Empty;
}

[SugarTable("Wcs_AssetHealthRootCauseAnalysis")]
public sealed class AssetHealthRootCauseAnalysisEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 64)]
    public string AnalysisId { get; set; } = string.Empty;

    [SugarColumn(Length = 32)]
    public string TriggerEventId { get; set; } = string.Empty;

    public int TriggerEventVersion { get; set; }

    [SugarColumn(Length = 128)]
    public string TriggerAssetId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string GraphVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string GraphHash { get; set; } = string.Empty;

    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public DateTime AnalyzedAtUtc { get; set; }
    public int ObservedEventCount { get; set; }
    public int CandidateCount { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? PrimaryRootCauseNodeId { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? PrimaryRootCauseEntityId { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? PrimaryConfidence { get; set; }

    public int ReviewDecision { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ReviewedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReviewedAtUtc { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ReviewNote { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SelectedRootCauseNodeId { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string AnalysisJson { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}

[SugarTable("Wcs_AssetHealthRootCauseReviewJournal")]
public sealed class AssetHealthRootCauseReviewEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 64)]
    public string ReviewId { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string AnalysisId { get; set; } = string.Empty;

    public int Decision { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? SelectedRootCauseNodeId { get; set; }

    [SugarColumn(Length = 128)]
    public string Actor { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? Note { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}

/// <summary>
/// v3.6 根因图版本、不可变分析快照和人工复核 Journal。
/// 每次操作创建独立 SqlSugarClient，不进入 PLC、任务或调度控制路径。
/// </summary>
public sealed class SqlSugarAssetHealthRootCauseAnalysisStore : IAssetHealthRootCauseAnalysisStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _connectionString;
    private readonly AssetHealthRootCauseOptions _options;
    private readonly ILogger<SqlSugarAssetHealthRootCauseAnalysisStore> _logger;
    private readonly object _schemaGate = new();
    private readonly object _statusGate = new();
    private bool _schemaReady;
    private DateTime _nextMaintenanceUtc = DateTime.MinValue;
    private DateTime? _lastSuccessfulWriteUtc;
    private string? _lastError;

    public SqlSugarAssetHealthRootCauseAnalysisStore(
        string connectionString,
        AssetHealthRootCauseOptions options,
        ILogger<SqlSugarAssetHealthRootCauseAnalysisStore> logger)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("WcsDb connection string is required.", nameof(connectionString))
            : connectionString;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => "SqlServer";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        return ValueTask.CompletedTask;
    }

    public ValueTask RegisterGraphAsync(
        RootCauseGraphRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        using var db = CreateClient();
        var existing = db.Queryable<AssetHealthRootCauseGraphVersionEntity>()
            .Where(row => row.Version == registration.Version)
            .First();
        if (existing is not null)
        {
            if (!string.Equals(existing.GraphHash, registration.GraphHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Root cause graph version {registration.Version} already exists with a different hash.");
            return ValueTask.CompletedTask;
        }

        db.Insertable(new AssetHealthRootCauseGraphVersionEntity
        {
            Version = registration.Version,
            GraphHash = registration.GraphHash,
            Source = registration.Source,
            ApprovedBy = registration.ApprovedBy,
            ApprovedAtUtc = registration.ApprovedAtUtc,
            RegisteredAtUtc = registration.RegisteredAtUtc,
            NodeCount = registration.NodeCount,
            EdgeCount = registration.EdgeCount,
            GraphJson = registration.GraphJson
        }).ExecuteCommand();
        MarkSuccessfulWrite();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SaveAsync(
        AssetHealthRootCauseAnalysisSnapshot analysis,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        using var db = CreateClient();
        if (db.Queryable<AssetHealthRootCauseAnalysisEntity>()
            .Any(row => row.AnalysisId == analysis.AnalysisId))
            return ValueTask.FromResult(false);

        db.Insertable(ToEntity(analysis)).ExecuteCommand();
        MarkSuccessfulWrite();
        return ValueTask.FromResult(true);
    }

    public ValueTask<AssetHealthRootCauseAnalysisSnapshot?> GetAsync(
        string analysisId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(analysisId))
            return ValueTask.FromResult<AssetHealthRootCauseAnalysisSnapshot?>(null);
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthRootCauseAnalysisEntity>()
            .Where(row => row.AnalysisId == analysisId.Trim())
            .First();
        return ValueTask.FromResult(entity is null ? null : ToSnapshot(entity));
    }

    public ValueTask<AssetHealthRootCauseAnalysisSnapshot?> GetLatestForTriggerAsync(
        string triggerEventId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(triggerEventId))
            return ValueTask.FromResult<AssetHealthRootCauseAnalysisSnapshot?>(null);
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthRootCauseAnalysisEntity>()
            .Where(row => row.TriggerEventId == triggerEventId.Trim())
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .First();
        return ValueTask.FromResult(entity is null ? null : ToSnapshot(entity));
    }

    public ValueTask<IReadOnlyList<AssetHealthRootCauseAnalysisSnapshot>> GetAnalysesAsync(
        string? triggerEventId = null,
        RootCauseReviewDecision? reviewDecision = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumAnalysesQueryCount);
        using var db = CreateClient();
        var query = db.Queryable<AssetHealthRootCauseAnalysisEntity>();
        if (!string.IsNullOrWhiteSpace(triggerEventId))
            query = query.Where(row => row.TriggerEventId == triggerEventId.Trim());
        if (reviewDecision is not null)
        {
            var decision = (int)reviewDecision.Value;
            query = query.Where(row => row.ReviewDecision == decision);
        }
        var items = query
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(maximumCount)
            .ToList()
            .Select(ToSnapshot)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AssetHealthRootCauseAnalysisSnapshot>>(items);
    }

    public ValueTask<AssetHealthRootCauseAnalysisSnapshot?> AppendReviewAsync(
        AssetHealthRootCauseReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        cancellationToken.ThrowIfCancellationRequested();
        if (review.Decision == RootCauseReviewDecision.Pending || string.IsNullOrWhiteSpace(review.Actor))
            throw new ArgumentException("A non-pending review decision and actor are required.", nameof(review));
        EnsureSchema();
        using var db = CreateClient();
        var analysis = db.Queryable<AssetHealthRootCauseAnalysisEntity>()
            .Where(row => row.AnalysisId == review.AnalysisId)
            .First();
        if (analysis is null)
            return ValueTask.FromResult<AssetHealthRootCauseAnalysisSnapshot?>(null);
        if (db.Queryable<AssetHealthRootCauseReviewEntity>().Any(row => row.ReviewId == review.ReviewId))
            return ValueTask.FromResult<AssetHealthRootCauseAnalysisSnapshot?>(ToSnapshot(analysis));

        try
        {
            db.Ado.BeginTran();
            db.Insertable(new AssetHealthRootCauseReviewEntity
            {
                ReviewId = review.ReviewId,
                AnalysisId = review.AnalysisId,
                Decision = (int)review.Decision,
                SelectedRootCauseNodeId = review.SelectedRootCauseNodeId,
                Actor = review.Actor,
                Note = review.Note,
                OccurredAtUtc = review.OccurredAtUtc
            }).ExecuteCommand();
            analysis.ReviewDecision = (int)review.Decision;
            analysis.ReviewedBy = review.Actor;
            analysis.ReviewedAtUtc = review.OccurredAtUtc;
            analysis.ReviewNote = review.Note;
            analysis.SelectedRootCauseNodeId = review.SelectedRootCauseNodeId;
            analysis.UpdatedAtUtc = review.OccurredAtUtc;
            db.Updateable(analysis).ExecuteCommand();
            db.Ado.CommitTran();
            MarkSuccessfulWrite();
            return ValueTask.FromResult<AssetHealthRootCauseAnalysisSnapshot?>(ToSnapshot(analysis));
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    public ValueTask<IReadOnlyList<AssetHealthRootCauseReview>> GetReviewsAsync(
        string analysisId,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(analysisId))
            return ValueTask.FromResult<IReadOnlyList<AssetHealthRootCauseReview>>(Array.Empty<AssetHealthRootCauseReview>());
        EnsureSchema();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumAnalysesQueryCount);
        using var db = CreateClient();
        var items = db.Queryable<AssetHealthRootCauseReviewEntity>()
            .Where(row => row.AnalysisId == analysisId.Trim())
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(maximumCount)
            .ToList()
            .Select(static row => new AssetHealthRootCauseReview
            {
                ReviewId = row.ReviewId,
                AnalysisId = row.AnalysisId,
                Decision = (RootCauseReviewDecision)row.Decision,
                SelectedRootCauseNodeId = row.SelectedRootCauseNodeId,
                Actor = row.Actor,
                Note = row.Note,
                OccurredAtUtc = row.OccurredAtUtc
            })
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AssetHealthRootCauseReview>>(items);
    }

    public ValueTask<AssetHealthRootCauseStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = false;
        var graphCount = 0;
        var analysisCount = 0;
        var reviewCount = 0;
        try
        {
            EnsureSchema();
            using var db = CreateClient();
            graphCount = db.Queryable<AssetHealthRootCauseGraphVersionEntity>().Count();
            analysisCount = db.Queryable<AssetHealthRootCauseAnalysisEntity>().Count();
            reviewCount = db.Queryable<AssetHealthRootCauseReviewEntity>().Count();
            available = true;
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
        }

        DateTime? lastWrite;
        string? lastError;
        lock (_statusGate)
        {
            lastWrite = _lastSuccessfulWriteUtc;
            lastError = _lastError;
        }
        return ValueTask.FromResult(new AssetHealthRootCauseStoreStatus
        {
            Enabled = _options.Enabled,
            Provider = Provider,
            IsAvailable = available,
            RegisteredGraphs = graphCount,
            RetainedAnalyses = analysisCount,
            RetainedReviews = reviewCount,
            LastSuccessfulWriteUtc = lastWrite,
            LastError = lastError
        });
    }

    public ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        utcNow = NormalizeUtc(utcNow);
        if (!_options.Enabled || utcNow < _nextMaintenanceUtc)
            return ValueTask.CompletedTask;
        _nextMaintenanceUtc = utcNow.AddSeconds(_options.MaintenanceIntervalSeconds);
        try
        {
            EnsureSchema();
            using var db = CreateClient();
            var parameters = new[]
            {
                new SugarParameter("@BatchSize", _options.MaintenanceBatchSize),
                new SugarParameter("@CutoffUtc", utcNow.AddHours(-_options.AnalysisRetentionHours))
            };
            db.Ado.ExecuteCommand(@"
DELETE TOP (@BatchSize) R
FROM Wcs_AssetHealthRootCauseReviewJournal R
INNER JOIN Wcs_AssetHealthRootCauseAnalysis A ON A.AnalysisId = R.AnalysisId
WHERE A.AnalyzedAtUtc < @CutoffUtc;
DELETE TOP (@BatchSize)
FROM Wcs_AssetHealthRootCauseAnalysis
WHERE AnalyzedAtUtc < @CutoffUtc;", parameters);
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
            _nextMaintenanceUtc = utcNow.AddSeconds(30);
            _logger.LogWarning(exception, "Asset health root cause maintenance failed.");
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_schemaGate)
        {
            if (_schemaReady) return;
            using var db = CreateClient();
            db.CodeFirst.InitTables(
                typeof(AssetHealthRootCauseGraphVersionEntity),
                typeof(AssetHealthRootCauseAnalysisEntity),
                typeof(AssetHealthRootCauseReviewEntity));
            db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_RootCauseGraph_Version' AND object_id = OBJECT_ID('Wcs_AssetHealthRootCauseGraphVersion'))
    CREATE UNIQUE INDEX UX_Wcs_RootCauseGraph_Version ON Wcs_AssetHealthRootCauseGraphVersion(Version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_RootCauseGraph_Hash' AND object_id = OBJECT_ID('Wcs_AssetHealthRootCauseGraphVersion'))
    CREATE UNIQUE INDEX UX_Wcs_RootCauseGraph_Hash ON Wcs_AssetHealthRootCauseGraphVersion(GraphHash);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_RootCauseAnalysis_Id' AND object_id = OBJECT_ID('Wcs_AssetHealthRootCauseAnalysis'))
    CREATE UNIQUE INDEX UX_Wcs_RootCauseAnalysis_Id ON Wcs_AssetHealthRootCauseAnalysis(AnalysisId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_RootCauseAnalysis_Trigger' AND object_id = OBJECT_ID('Wcs_AssetHealthRootCauseAnalysis'))
    CREATE INDEX IX_Wcs_RootCauseAnalysis_Trigger ON Wcs_AssetHealthRootCauseAnalysis(TriggerEventId, Sequence DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_RootCauseAnalysis_Time' AND object_id = OBJECT_ID('Wcs_AssetHealthRootCauseAnalysis'))
    CREATE INDEX IX_Wcs_RootCauseAnalysis_Time ON Wcs_AssetHealthRootCauseAnalysis(AnalyzedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_RootCauseReview_Id' AND object_id = OBJECT_ID('Wcs_AssetHealthRootCauseReviewJournal'))
    CREATE UNIQUE INDEX UX_Wcs_RootCauseReview_Id ON Wcs_AssetHealthRootCauseReviewJournal(ReviewId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_RootCauseReview_Analysis' AND object_id = OBJECT_ID('Wcs_AssetHealthRootCauseReviewJournal'))
    CREATE INDEX IX_Wcs_RootCauseReview_Analysis ON Wcs_AssetHealthRootCauseReviewJournal(AnalysisId, Sequence DESC);");
            _schemaReady = true;
        }
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private static AssetHealthRootCauseAnalysisEntity ToEntity(
        AssetHealthRootCauseAnalysisSnapshot analysis) => new()
    {
        AnalysisId = analysis.AnalysisId,
        TriggerEventId = analysis.TriggerEventId,
        TriggerEventVersion = analysis.TriggerEventVersion,
        TriggerAssetId = analysis.TriggerAssetId,
        GraphVersion = analysis.GraphVersion,
        GraphHash = analysis.GraphHash,
        WindowStartUtc = analysis.WindowStartUtc,
        WindowEndUtc = analysis.WindowEndUtc,
        AnalyzedAtUtc = analysis.AnalyzedAtUtc,
        ObservedEventCount = analysis.ObservedEventCount,
        CandidateCount = analysis.Candidates.Count,
        PrimaryRootCauseNodeId = analysis.PrimaryCandidate?.NodeId,
        PrimaryRootCauseEntityId = analysis.PrimaryCandidate?.EntityId,
        PrimaryConfidence = analysis.PrimaryCandidate?.Confidence,
        ReviewDecision = (int)analysis.ReviewDecision,
        ReviewedBy = analysis.ReviewedBy,
        ReviewedAtUtc = analysis.ReviewedAtUtc,
        ReviewNote = analysis.ReviewNote,
        SelectedRootCauseNodeId = analysis.SelectedRootCauseNodeId,
        AnalysisJson = JsonSerializer.Serialize(analysis, JsonOptions),
        UpdatedAtUtc = analysis.AnalyzedAtUtc
    };

    private static AssetHealthRootCauseAnalysisSnapshot ToSnapshot(
        AssetHealthRootCauseAnalysisEntity entity)
    {
        var snapshot = JsonSerializer.Deserialize<AssetHealthRootCauseAnalysisSnapshot>(
            entity.AnalysisJson,
            JsonOptions) ?? throw new InvalidOperationException(
                $"Root cause analysis JSON is invalid: {entity.AnalysisId}.");
        return snapshot with
        {
            ReviewDecision = (RootCauseReviewDecision)entity.ReviewDecision,
            ReviewedBy = entity.ReviewedBy,
            ReviewedAtUtc = entity.ReviewedAtUtc,
            ReviewNote = entity.ReviewNote,
            SelectedRootCauseNodeId = entity.SelectedRootCauseNodeId
        };
    }

    private void MarkSuccessfulWrite()
    {
        lock (_statusGate)
        {
            _lastSuccessfulWriteUtc = DateTime.UtcNow;
            _lastError = null;
        }
    }

    private void SetLastError(string error)
    {
        lock (_statusGate) _lastError = error;
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
