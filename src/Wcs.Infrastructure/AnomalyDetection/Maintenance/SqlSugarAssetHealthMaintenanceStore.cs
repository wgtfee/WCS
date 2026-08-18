namespace Wcs.Infrastructure.AnomalyDetection.Maintenance;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SqlSugar;
using Wcs.Core.AnomalyDetection.Maintenance;

[SugarTable("Wcs_AssetHealthMaintenanceRuleSetVersion")]
public sealed class AssetHealthMaintenanceRuleSetVersionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 128)]
    public string Version { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string RuleSetHash { get; set; } = string.Empty;

    [SugarColumn(Length = 256)]
    public string Source { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string ApprovedBy { get; set; } = string.Empty;

    public DateTime ApprovedAtUtc { get; set; }
    public DateTime RegisteredAtUtc { get; set; }
    public int RuleCount { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RuleSetJson { get; set; } = string.Empty;
}

[SugarTable("Wcs_AssetHealthMaintenanceRecommendation")]
public sealed class AssetHealthMaintenanceRecommendationEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 64)]
    public string RecommendationId { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string AnalysisId { get; set; } = string.Empty;

    [SugarColumn(Length = 32)]
    public string TriggerEventId { get; set; } = string.Empty;

    public int TriggerEventVersion { get; set; }

    [SugarColumn(Length = 128)]
    public string AssetId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string RuleSetVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string RuleSetHash { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string RuleId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string RootCauseNodeId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string RootCauseEntityId { get; set; } = string.Empty;

    public double RootCauseConfidence { get; set; }
    public int RootCauseReviewDecision { get; set; }
    public int EventGrade { get; set; }
    public double PreMaintenanceHealthScore { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? PostMaintenanceHealthScore { get; set; }

    public int Status { get; set; }
    public int Priority { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MesWorkOrderNo { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? AssignedTo { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CompletedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? LatestFeedbackDecision { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? LatestFeedbackActor { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LatestFeedbackAtUtc { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? LatestFeedbackNote { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string RecommendationJson { get; set; } = string.Empty;
}

[SugarTable("Wcs_AssetHealthMaintenanceFeedbackJournal")]
public sealed class AssetHealthMaintenanceFeedbackEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 64)]
    public string FeedbackId { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string RecommendationId { get; set; } = string.Empty;

    public int Decision { get; set; }

    [SugarColumn(Length = 128)]
    public string Actor { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? PostHealthScore { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? MesWorkOrderNo { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? AssignedTo { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CompletedAtUtc { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? Note { get; set; }
}

[SugarTable("Wcs_AssetHealthMaintenanceTrainingLabel")]
public sealed class AssetHealthMaintenanceTrainingLabelEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 64)]
    public string CandidateId { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string RecommendationId { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string AnalysisId { get; set; } = string.Empty;

    [SugarColumn(Length = 32)]
    public string EventId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string AssetId { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string RootCauseNodeId { get; set; } = string.Empty;

    [SugarColumn(Length = 64)]
    public string Label { get; set; } = string.Empty;

    public int SourceDecision { get; set; }
    public int Status { get; set; }

    [SugarColumn(Length = 128)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ReviewedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? ReviewedAtUtc { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ReviewNote { get; set; }
}

/// <summary>
/// v3.7 已审批规则、建议快照、反馈 Journal 和候选训练标签。
/// 每次操作使用独立 SqlSugarClient，完全位于 PLC、任务和调度控制链路之外。
/// </summary>
public sealed class SqlSugarAssetHealthMaintenanceStore : IAssetHealthMaintenanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _connectionString;
    private readonly AssetHealthMaintenanceOptions _options;
    private readonly ILogger<SqlSugarAssetHealthMaintenanceStore> _logger;
    private readonly object _schemaGate = new();
    private readonly object _statusGate = new();
    private bool _schemaReady;
    private DateTime _nextMaintenanceUtc = DateTime.MinValue;
    private DateTime? _lastSuccessfulWriteUtc;
    private string? _lastError;

    public SqlSugarAssetHealthMaintenanceStore(
        string connectionString,
        AssetHealthMaintenanceOptions options,
        ILogger<SqlSugarAssetHealthMaintenanceStore> logger)
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

    public ValueTask RegisterRuleSetAsync(
        MaintenanceRuleSetRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        using var db = CreateClient();
        var existing = db.Queryable<AssetHealthMaintenanceRuleSetVersionEntity>()
            .Where(row => row.Version == registration.Version)
            .First();
        if (existing is not null)
        {
            if (!string.Equals(existing.RuleSetHash, registration.RuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Maintenance rule-set version {registration.Version} already exists with a different hash.");
            return ValueTask.CompletedTask;
        }

        db.Insertable(new AssetHealthMaintenanceRuleSetVersionEntity
        {
            Version = registration.Version,
            RuleSetHash = registration.RuleSetHash,
            Source = registration.Source,
            ApprovedBy = registration.ApprovedBy,
            ApprovedAtUtc = registration.ApprovedAtUtc,
            RegisteredAtUtc = registration.RegisteredAtUtc,
            RuleCount = registration.RuleCount,
            RuleSetJson = registration.RuleSetJson
        }).ExecuteCommand();
        MarkSuccessfulWrite();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> SaveRecommendationAsync(
        AssetHealthMaintenanceRecommendation recommendation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        using var db = CreateClient();
        if (db.Queryable<AssetHealthMaintenanceRecommendationEntity>()
            .Any(row => row.RecommendationId == recommendation.RecommendationId))
            return ValueTask.FromResult(false);

        db.Insertable(ToEntity(recommendation)).ExecuteCommand();
        MarkSuccessfulWrite();
        return ValueTask.FromResult(true);
    }

    public ValueTask<AssetHealthMaintenanceRecommendation?> GetRecommendationAsync(
        string recommendationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(recommendationId))
            return ValueTask.FromResult<AssetHealthMaintenanceRecommendation?>(null);
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthMaintenanceRecommendationEntity>()
            .Where(row => row.RecommendationId == recommendationId.Trim())
            .First();
        return ValueTask.FromResult(entity is null ? null : ToSnapshot(entity));
    }

    public ValueTask<AssetHealthMaintenanceRecommendation?> GetLatestForAnalysisAsync(
        string analysisId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(analysisId))
            return ValueTask.FromResult<AssetHealthMaintenanceRecommendation?>(null);
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthMaintenanceRecommendationEntity>()
            .Where(row => row.AnalysisId == analysisId.Trim())
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .First();
        return ValueTask.FromResult(entity is null ? null : ToSnapshot(entity));
    }

    public ValueTask<IReadOnlyList<AssetHealthMaintenanceRecommendation>> GetRecommendationsAsync(
        MaintenanceRecommendationStatus? status = null,
        string? assetId = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumRecommendationsQueryCount);
        using var db = CreateClient();
        var query = db.Queryable<AssetHealthMaintenanceRecommendationEntity>();
        if (status is not null)
        {
            var value = (int)status.Value;
            query = query.Where(row => row.Status == value);
        }
        if (!string.IsNullOrWhiteSpace(assetId))
            query = query.Where(row => row.AssetId == assetId.Trim());
        var items = query
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(maximumCount)
            .ToList()
            .Select(ToSnapshot)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AssetHealthMaintenanceRecommendation>>(items);
    }

    public ValueTask<AssetHealthMaintenanceRecommendation?> AppendFeedbackAsync(
        AssetHealthMaintenanceFeedback feedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(feedback.Actor))
            throw new ArgumentException("Feedback actor is required.", nameof(feedback));
        EnsureSchema();
        using var db = CreateClient();
        var recommendation = db.Queryable<AssetHealthMaintenanceRecommendationEntity>()
            .Where(row => row.RecommendationId == feedback.RecommendationId)
            .First();
        if (recommendation is null)
            return ValueTask.FromResult<AssetHealthMaintenanceRecommendation?>(null);
        if (db.Queryable<AssetHealthMaintenanceFeedbackEntity>()
            .Any(row => row.FeedbackId == feedback.FeedbackId))
            return ValueTask.FromResult<AssetHealthMaintenanceRecommendation?>(ToSnapshot(recommendation));

        try
        {
            db.Ado.BeginTran();
            db.Insertable(ToFeedbackEntity(feedback)).ExecuteCommand();
            ApplyFeedback(recommendation, feedback);
            db.Updateable(recommendation).ExecuteCommand();
            var label = CreateTrainingLabel(recommendation, feedback);
            if (label is not null && !db.Queryable<AssetHealthMaintenanceTrainingLabelEntity>()
                    .Any(row => row.CandidateId == label.CandidateId))
                db.Insertable(label).ExecuteCommand();
            db.Ado.CommitTran();
            MarkSuccessfulWrite();
            return ValueTask.FromResult<AssetHealthMaintenanceRecommendation?>(ToSnapshot(recommendation));
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    public ValueTask<IReadOnlyList<AssetHealthMaintenanceFeedback>> GetFeedbackAsync(
        string recommendationId,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(recommendationId))
            return ValueTask.FromResult<IReadOnlyList<AssetHealthMaintenanceFeedback>>(
                Array.Empty<AssetHealthMaintenanceFeedback>());
        EnsureSchema();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumRecommendationsQueryCount);
        using var db = CreateClient();
        var items = db.Queryable<AssetHealthMaintenanceFeedbackEntity>()
            .Where(row => row.RecommendationId == recommendationId.Trim())
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(maximumCount)
            .ToList()
            .Select(ToFeedback)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AssetHealthMaintenanceFeedback>>(items);
    }

    public ValueTask<IReadOnlyList<MaintenanceTrainingLabelCandidate>> GetTrainingLabelCandidatesAsync(
        MaintenanceTrainingLabelStatus? status = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumRecommendationsQueryCount);
        using var db = CreateClient();
        var query = db.Queryable<AssetHealthMaintenanceTrainingLabelEntity>();
        if (status is not null)
        {
            var value = (int)status.Value;
            query = query.Where(row => row.Status == value);
        }
        var items = query
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(maximumCount)
            .ToList()
            .Select(ToTrainingLabel)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<MaintenanceTrainingLabelCandidate>>(items);
    }

    public ValueTask<MaintenanceTrainingLabelCandidate?> ReviewTrainingLabelAsync(
        string candidateId,
        MaintenanceTrainingLabelStatus status,
        string actor,
        string? note,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (status == MaintenanceTrainingLabelStatus.PendingApproval)
            throw new ArgumentException("Training label review must approve or reject.", nameof(status));
        if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Candidate id and actor are required.");
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthMaintenanceTrainingLabelEntity>()
            .Where(row => row.CandidateId == candidateId.Trim())
            .First();
        if (entity is null)
            return ValueTask.FromResult<MaintenanceTrainingLabelCandidate?>(null);
        entity.Status = (int)status;
        entity.ReviewedBy = actor.Trim();
        entity.ReviewedAtUtc = utcNow;
        entity.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        db.Updateable(entity).ExecuteCommand();
        MarkSuccessfulWrite();
        return ValueTask.FromResult<MaintenanceTrainingLabelCandidate?>(ToTrainingLabel(entity));
    }

    public ValueTask<AssetHealthMaintenanceMetrics> GetMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        using var db = CreateClient();
        var recommendations = db.Queryable<AssetHealthMaintenanceRecommendationEntity>().ToList();
        var feedback = db.Queryable<AssetHealthMaintenanceFeedbackEntity>().ToList();
        var total = recommendations.Count;
        var acceptedRecommendationIds = feedback
            .Where(row => row.Decision == (int)MaintenanceFeedbackDecision.Accepted)
            .Select(row => row.RecommendationId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var repaired = feedback.Count(row => row.Decision == (int)MaintenanceFeedbackDecision.Repaired);
        var falsePositive = feedback.Count(row => row.Decision == (int)MaintenanceFeedbackDecision.FalsePositive);
        var noFault = feedback.Count(row => row.Decision == (int)MaintenanceFeedbackDecision.NoFaultFound);
        var assessed = repaired + falsePositive + noFault;
        var closureMinutes = recommendations
            .Where(row => row.CompletedAtUtc.HasValue)
            .Select(row => (row.CompletedAtUtc!.Value - row.CreatedAtUtc).TotalMinutes)
            .Where(value => value >= 0)
            .ToArray();

        return ValueTask.FromResult(new AssetHealthMaintenanceMetrics
        {
            TotalRecommendations = total,
            AcceptedRecommendations = acceptedRecommendationIds,
            RejectedRecommendations = feedback
                .Where(row => row.Decision == (int)MaintenanceFeedbackDecision.Rejected)
                .Select(row => row.RecommendationId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            CompletedRecommendations = recommendations.Count(row =>
                row.Status == (int)MaintenanceRecommendationStatus.Completed),
            FalsePositiveCount = falsePositive,
            RepairedCount = repaired,
            NoFaultFoundCount = noFault,
            AcceptanceRate = Ratio(acceptedRecommendationIds, total),
            ConfirmedFaultRate = Ratio(repaired, assessed),
            FalsePositiveRate = Ratio(falsePositive, assessed),
            AverageClosureMinutes = closureMinutes.Length == 0 ? null : closureMinutes.Average()
        });
    }

    public ValueTask<AssetHealthMaintenanceStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = false;
        var ruleSets = 0;
        var recommendations = 0;
        var feedback = 0;
        var labels = 0;
        try
        {
            EnsureSchema();
            using var db = CreateClient();
            ruleSets = db.Queryable<AssetHealthMaintenanceRuleSetVersionEntity>().Count();
            recommendations = db.Queryable<AssetHealthMaintenanceRecommendationEntity>().Count();
            feedback = db.Queryable<AssetHealthMaintenanceFeedbackEntity>().Count();
            labels = db.Queryable<AssetHealthMaintenanceTrainingLabelEntity>()
                .Count(row => row.Status == (int)MaintenanceTrainingLabelStatus.PendingApproval);
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
        return ValueTask.FromResult(new AssetHealthMaintenanceStoreStatus
        {
            Enabled = _options.Enabled,
            Provider = Provider,
            IsAvailable = available,
            RegisteredRuleSets = ruleSets,
            RetainedRecommendations = recommendations,
            RetainedFeedbackRows = feedback,
            PendingTrainingLabels = labels,
            LastSuccessfulWriteUtc = lastWrite,
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
        _nextMaintenanceUtc = utcNow.AddSeconds(_options.MaintenanceIntervalSeconds);
        try
        {
            EnsureSchema();
            using var db = CreateClient();
            var parameters = new[]
            {
                new SugarParameter("@BatchSize", _options.MaintenanceBatchSize),
                new SugarParameter("@CutoffUtc", utcNow.AddHours(-_options.RecommendationRetentionHours))
            };
            db.Ado.ExecuteCommand(@"
DELETE TOP (@BatchSize) L
FROM Wcs_AssetHealthMaintenanceTrainingLabel L
INNER JOIN Wcs_AssetHealthMaintenanceRecommendation R ON R.RecommendationId = L.RecommendationId
WHERE R.CreatedAtUtc < @CutoffUtc AND L.Status <> 0;
DELETE TOP (@BatchSize) F
FROM Wcs_AssetHealthMaintenanceFeedbackJournal F
INNER JOIN Wcs_AssetHealthMaintenanceRecommendation R ON R.RecommendationId = F.RecommendationId
WHERE R.CreatedAtUtc < @CutoffUtc;
DELETE TOP (@BatchSize)
FROM Wcs_AssetHealthMaintenanceRecommendation
WHERE CreatedAtUtc < @CutoffUtc;", parameters);
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
            _nextMaintenanceUtc = utcNow.AddSeconds(30);
            _logger.LogWarning(exception, "Asset health maintenance cleanup failed.");
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
                typeof(AssetHealthMaintenanceRuleSetVersionEntity),
                typeof(AssetHealthMaintenanceRecommendationEntity),
                typeof(AssetHealthMaintenanceFeedbackEntity),
                typeof(AssetHealthMaintenanceTrainingLabelEntity));
            db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_MaintRuleSet_Version' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceRuleSetVersion'))
    CREATE UNIQUE INDEX UX_Wcs_MaintRuleSet_Version ON Wcs_AssetHealthMaintenanceRuleSetVersion(Version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_MaintRuleSet_Hash' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceRuleSetVersion'))
    CREATE UNIQUE INDEX UX_Wcs_MaintRuleSet_Hash ON Wcs_AssetHealthMaintenanceRuleSetVersion(RuleSetHash);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_MaintRecommendation_Id' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceRecommendation'))
    CREATE UNIQUE INDEX UX_Wcs_MaintRecommendation_Id ON Wcs_AssetHealthMaintenanceRecommendation(RecommendationId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_MaintRecommendation_Analysis' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceRecommendation'))
    CREATE INDEX IX_Wcs_MaintRecommendation_Analysis ON Wcs_AssetHealthMaintenanceRecommendation(AnalysisId, Sequence DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_MaintRecommendation_Asset' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceRecommendation'))
    CREATE INDEX IX_Wcs_MaintRecommendation_Asset ON Wcs_AssetHealthMaintenanceRecommendation(AssetId, Status, Sequence DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_MaintFeedback_Id' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceFeedbackJournal'))
    CREATE UNIQUE INDEX UX_Wcs_MaintFeedback_Id ON Wcs_AssetHealthMaintenanceFeedbackJournal(FeedbackId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_MaintFeedback_Recommendation' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceFeedbackJournal'))
    CREATE INDEX IX_Wcs_MaintFeedback_Recommendation ON Wcs_AssetHealthMaintenanceFeedbackJournal(RecommendationId, Sequence DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_MaintTrainingLabel_Id' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceTrainingLabel'))
    CREATE UNIQUE INDEX UX_Wcs_MaintTrainingLabel_Id ON Wcs_AssetHealthMaintenanceTrainingLabel(CandidateId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_MaintTrainingLabel_Status' AND object_id = OBJECT_ID('Wcs_AssetHealthMaintenanceTrainingLabel'))
    CREATE INDEX IX_Wcs_MaintTrainingLabel_Status ON Wcs_AssetHealthMaintenanceTrainingLabel(Status, Sequence DESC);");
            _schemaReady = true;
        }
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private static AssetHealthMaintenanceRecommendationEntity ToEntity(
        AssetHealthMaintenanceRecommendation recommendation) => new()
    {
        RecommendationId = recommendation.RecommendationId,
        AnalysisId = recommendation.AnalysisId,
        TriggerEventId = recommendation.TriggerEventId,
        TriggerEventVersion = recommendation.TriggerEventVersion,
        AssetId = recommendation.AssetId,
        RuleSetVersion = recommendation.RuleSetVersion,
        RuleSetHash = recommendation.RuleSetHash,
        RuleId = recommendation.RuleId,
        RootCauseNodeId = recommendation.RootCauseNodeId,
        RootCauseEntityId = recommendation.RootCauseEntityId,
        RootCauseConfidence = recommendation.RootCauseConfidence,
        RootCauseReviewDecision = (int)recommendation.RootCauseReviewDecision,
        EventGrade = (int)recommendation.EventGrade,
        PreMaintenanceHealthScore = recommendation.PreMaintenanceHealthScore,
        PostMaintenanceHealthScore = recommendation.PostMaintenanceHealthScore,
        Status = (int)recommendation.Status,
        Priority = recommendation.Priority,
        CreatedAtUtc = recommendation.CreatedAtUtc,
        UpdatedAtUtc = recommendation.UpdatedAtUtc ?? recommendation.CreatedAtUtc,
        MesWorkOrderNo = recommendation.MesWorkOrderNo,
        AssignedTo = recommendation.AssignedTo,
        CompletedAtUtc = recommendation.CompletedAtUtc,
        LatestFeedbackDecision = recommendation.LatestFeedbackDecision is null
            ? null
            : (int)recommendation.LatestFeedbackDecision.Value,
        LatestFeedbackActor = recommendation.LatestFeedbackActor,
        LatestFeedbackAtUtc = recommendation.LatestFeedbackAtUtc,
        LatestFeedbackNote = recommendation.LatestFeedbackNote,
        RecommendationJson = JsonSerializer.Serialize(recommendation, JsonOptions)
    };

    private static AssetHealthMaintenanceRecommendation ToSnapshot(
        AssetHealthMaintenanceRecommendationEntity entity)
    {
        var snapshot = JsonSerializer.Deserialize<AssetHealthMaintenanceRecommendation>(
            entity.RecommendationJson,
            JsonOptions) ?? throw new InvalidOperationException(
                $"Maintenance recommendation JSON is invalid: {entity.RecommendationId}.");
        return snapshot with
        {
            PostMaintenanceHealthScore = entity.PostMaintenanceHealthScore,
            Status = (MaintenanceRecommendationStatus)entity.Status,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            MesWorkOrderNo = entity.MesWorkOrderNo,
            AssignedTo = entity.AssignedTo,
            CompletedAtUtc = entity.CompletedAtUtc,
            LatestFeedbackDecision = entity.LatestFeedbackDecision is null
                ? null
                : (MaintenanceFeedbackDecision)entity.LatestFeedbackDecision.Value,
            LatestFeedbackActor = entity.LatestFeedbackActor,
            LatestFeedbackAtUtc = entity.LatestFeedbackAtUtc,
            LatestFeedbackNote = entity.LatestFeedbackNote
        };
    }

    private static AssetHealthMaintenanceFeedbackEntity ToFeedbackEntity(
        AssetHealthMaintenanceFeedback feedback) => new()
    {
        FeedbackId = feedback.FeedbackId,
        RecommendationId = feedback.RecommendationId,
        Decision = (int)feedback.Decision,
        Actor = feedback.Actor.Trim(),
        OccurredAtUtc = feedback.OccurredAtUtc,
        PostHealthScore = feedback.PostHealthScore,
        MesWorkOrderNo = NullIfWhiteSpace(feedback.MesWorkOrderNo),
        AssignedTo = NullIfWhiteSpace(feedback.AssignedTo),
        CompletedAtUtc = feedback.CompletedAtUtc,
        Note = NullIfWhiteSpace(feedback.Note)
    };

    private static AssetHealthMaintenanceFeedback ToFeedback(
        AssetHealthMaintenanceFeedbackEntity entity) => new()
    {
        FeedbackId = entity.FeedbackId,
        RecommendationId = entity.RecommendationId,
        Decision = (MaintenanceFeedbackDecision)entity.Decision,
        Actor = entity.Actor,
        OccurredAtUtc = entity.OccurredAtUtc,
        PostHealthScore = entity.PostHealthScore,
        MesWorkOrderNo = entity.MesWorkOrderNo,
        AssignedTo = entity.AssignedTo,
        CompletedAtUtc = entity.CompletedAtUtc,
        Note = entity.Note
    };

    private static void ApplyFeedback(
        AssetHealthMaintenanceRecommendationEntity recommendation,
        AssetHealthMaintenanceFeedback feedback)
    {
        recommendation.Status = feedback.Decision switch
        {
            MaintenanceFeedbackDecision.Accepted => (int)MaintenanceRecommendationStatus.Accepted,
            MaintenanceFeedbackDecision.Rejected => (int)MaintenanceRecommendationStatus.Rejected,
            MaintenanceFeedbackDecision.FalsePositive => (int)MaintenanceRecommendationStatus.Completed,
            MaintenanceFeedbackDecision.Repaired => (int)MaintenanceRecommendationStatus.Completed,
            MaintenanceFeedbackDecision.NoFaultFound => (int)MaintenanceRecommendationStatus.Completed,
            MaintenanceFeedbackDecision.Cancelled => (int)MaintenanceRecommendationStatus.Cancelled,
            _ => recommendation.Status
        };
        recommendation.PostMaintenanceHealthScore = feedback.PostHealthScore ?? recommendation.PostMaintenanceHealthScore;
        recommendation.MesWorkOrderNo = NullIfWhiteSpace(feedback.MesWorkOrderNo) ?? recommendation.MesWorkOrderNo;
        recommendation.AssignedTo = NullIfWhiteSpace(feedback.AssignedTo) ?? recommendation.AssignedTo;
        recommendation.CompletedAtUtc = feedback.CompletedAtUtc ??
            (feedback.Decision is MaintenanceFeedbackDecision.FalsePositive or
                MaintenanceFeedbackDecision.Repaired or
                MaintenanceFeedbackDecision.NoFaultFound
                    ? feedback.OccurredAtUtc
                    : recommendation.CompletedAtUtc);
        recommendation.LatestFeedbackDecision = (int)feedback.Decision;
        recommendation.LatestFeedbackActor = feedback.Actor.Trim();
        recommendation.LatestFeedbackAtUtc = feedback.OccurredAtUtc;
        recommendation.LatestFeedbackNote = NullIfWhiteSpace(feedback.Note);
        recommendation.UpdatedAtUtc = feedback.OccurredAtUtc;
    }

    private static AssetHealthMaintenanceTrainingLabelEntity? CreateTrainingLabel(
        AssetHealthMaintenanceRecommendationEntity recommendation,
        AssetHealthMaintenanceFeedback feedback)
    {
        var label = feedback.Decision switch
        {
            MaintenanceFeedbackDecision.Repaired => "fault-confirmed",
            MaintenanceFeedbackDecision.FalsePositive => "false-positive",
            MaintenanceFeedbackDecision.NoFaultFound => "no-fault-found",
            _ => null
        };
        if (label is null)
            return null;
        var candidateId = Sha256(string.Join('|',
            recommendation.RecommendationId,
            feedback.FeedbackId,
            label));
        return new AssetHealthMaintenanceTrainingLabelEntity
        {
            CandidateId = candidateId,
            RecommendationId = recommendation.RecommendationId,
            AnalysisId = recommendation.AnalysisId,
            EventId = recommendation.TriggerEventId,
            AssetId = recommendation.AssetId,
            RootCauseNodeId = recommendation.RootCauseNodeId,
            Label = label,
            SourceDecision = (int)feedback.Decision,
            Status = (int)MaintenanceTrainingLabelStatus.PendingApproval,
            CreatedBy = feedback.Actor.Trim(),
            CreatedAtUtc = feedback.OccurredAtUtc
        };
    }

    private static MaintenanceTrainingLabelCandidate ToTrainingLabel(
        AssetHealthMaintenanceTrainingLabelEntity entity) => new()
    {
        CandidateId = entity.CandidateId,
        RecommendationId = entity.RecommendationId,
        AnalysisId = entity.AnalysisId,
        EventId = entity.EventId,
        AssetId = entity.AssetId,
        RootCauseNodeId = entity.RootCauseNodeId,
        Label = entity.Label,
        SourceDecision = (MaintenanceFeedbackDecision)entity.SourceDecision,
        Status = (MaintenanceTrainingLabelStatus)entity.Status,
        CreatedBy = entity.CreatedBy,
        CreatedAtUtc = entity.CreatedAtUtc,
        ReviewedBy = entity.ReviewedBy,
        ReviewedAtUtc = entity.ReviewedAtUtc,
        ReviewNote = entity.ReviewNote
    };

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

    private static double Ratio(int numerator, int denominator) =>
        denominator <= 0 ? 0 : Math.Round((double)numerator / denominator, 6);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
