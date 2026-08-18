namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning;

using SqlSugar;
using Wcs.Core.AnomalyDetection.MachineLearning;

public sealed class SqlSugarPlcMlGovernanceStore : IPlcMlGovernanceStore
{
    private readonly string _connectionString;

    public SqlSugarPlcMlGovernanceStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task UpsertCandidateAsync(PlcMlCandidateRecord candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = ToEntity(candidate);
        if (db.Queryable<PlcMlCandidateEntity>().Any(x => x.CandidateId == candidate.CandidateId))
            db.Updateable(entity).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task RecoverCandidateAsync(
        string candidateId,
        DateTime recoveredUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var affected = db.Updateable<PlcMlCandidateEntity>()
            .SetColumns(x => x.IsActive == false)
            .SetColumns(x => x.RecoveredUtc == recoveredUtc)
            .Where(x => x.CandidateId == candidateId)
            .ExecuteCommand();
        if (affected == 0) throw new KeyNotFoundException($"未找到 ML 候选：{candidateId}。");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(
        string? profileId,
        PlcMlReviewDecision? decision,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var query = db.Queryable<PlcMlCandidateEntity>();
        if (!string.IsNullOrWhiteSpace(profileId))
            query = query.Where(x => x.ProfileId == profileId);
        if (decision is not null)
        {
            var value = (int)decision.Value;
            query = query.Where(x => x.ReviewDecision == value);
        }
        IReadOnlyList<PlcMlCandidateRecord> result = query
            .OrderBy(x => x.DetectedUtc, OrderByType.Desc)
            .Take(Math.Clamp(maximumCount, 1, 5000))
            .ToList()
            .Select(ToRecord)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<PlcMlCandidateRecord> ReviewCandidateAsync(
        string candidateId,
        PlcMlReviewDecision decision,
        string reviewedBy,
        string? comment,
        DateTime reviewedUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (decision == PlcMlReviewDecision.Unreviewed)
            throw new ArgumentException("人工复核不能提交 Unreviewed。", nameof(decision));
        if (string.IsNullOrWhiteSpace(reviewedBy))
            throw new ArgumentException("复核人不能为空。", nameof(reviewedBy));

        using var db = CreateClient();
        var entity = db.Queryable<PlcMlCandidateEntity>().Where(x => x.CandidateId == candidateId).First()
            ?? throw new KeyNotFoundException($"未找到 ML 候选：{candidateId}。");
        entity.ReviewDecision = (int)decision;
        entity.ReviewedBy = reviewedBy.Trim();
        entity.ReviewedUtc = reviewedUtc;
        entity.ReviewComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        db.Updateable(entity).ExecuteCommand();
        return Task.FromResult(ToRecord(entity));
    }

    public Task RegisterModelAsync(PlcMlModelGovernanceInfo model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = ToEntity(model);
        if (db.Queryable<PlcMlModelGovernanceEntity>().Any(x => x.GovernanceId == model.GovernanceId))
            db.Updateable(entity).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task<PlcMlModelGovernanceInfo?> GetModelAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = db.Queryable<PlcMlModelGovernanceEntity>()
            .Where(x => x.ProfileId == profileId && x.ModelVersion == version)
            .First();
        return Task.FromResult(entity is null ? null : ToInfo(entity));
    }

    public Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelsAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        IReadOnlyList<PlcMlModelGovernanceInfo> result = db.Queryable<PlcMlModelGovernanceEntity>()
            .Where(x => x.ProfileId == profileId)
            .OrderBy(x => x.RequestedUtc, OrderByType.Desc)
            .ToList()
            .Select(ToInfo)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<PlcMlModelGovernanceInfo> DecideModelAsync(
        string profileId,
        string version,
        PlcMlApprovalStatus status,
        string actor,
        string? comment,
        DateTime decidedUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (status == PlcMlApprovalStatus.Pending)
            throw new ArgumentException("审批结果不能是 Pending。", nameof(status));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("审批人不能为空。", nameof(actor));

        using var db = CreateClient();
        var entity = db.Queryable<PlcMlModelGovernanceEntity>()
            .Where(x => x.ProfileId == profileId && x.ModelVersion == version)
            .First() ?? throw new KeyNotFoundException($"未找到待审批模型：Profile={profileId}, Version={version}。");
        entity.ApprovalStatus = (int)status;
        entity.DecidedUtc = decidedUtc;
        entity.DecidedBy = actor.Trim();
        entity.DecisionComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        db.Updateable(entity).ExecuteCommand();
        return Task.FromResult(ToInfo(entity));
    }

    public Task<bool> IsModelApprovedAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var approved = (int)PlcMlApprovalStatus.Approved;
        return Task.FromResult(db.Queryable<PlcMlModelGovernanceEntity>()
            .Any(x => x.ProfileId == profileId && x.ModelVersion == version && x.ApprovalStatus == approved));
    }

    public Task SaveDriftSnapshotAsync(PlcMlDriftSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = ToEntity(snapshot);
        if (db.Queryable<PlcMlDriftSnapshotEntity>().Any(x => x.SnapshotId == snapshot.SnapshotId))
            db.Updateable(entity).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = db.Queryable<PlcMlDriftSnapshotEntity>()
            .Where(x => x.ProfileId == profileId)
            .OrderBy(x => x.CalculatedUtc, OrderByType.Desc)
            .First();
        return Task.FromResult(entity is null ? null : ToSnapshot(entity));
    }

    public Task<PlcMlEvaluationSummary> GetEvaluationAsync(
        string profileId,
        string? modelVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var query = db.Queryable<PlcMlCandidateEntity>().Where(x => x.ProfileId == profileId);
        if (!string.IsNullOrWhiteSpace(modelVersion))
            query = query.Where(x => x.ModelVersion == modelVersion);
        var rows = query.ToList();
        var truePositive = rows.Count(x => x.ReviewDecision == (int)PlcMlReviewDecision.TruePositive);
        var falsePositive = rows.Count(x => x.ReviewDecision == (int)PlcMlReviewDecision.FalsePositive);
        var expected = rows.Count(x => x.ReviewDecision == (int)PlcMlReviewDecision.ExpectedBehavior);
        var investigation = rows.Count(x => x.ReviewDecision == (int)PlcMlReviewDecision.NeedsInvestigation);
        var unreviewed = rows.Count(x => x.ReviewDecision == (int)PlcMlReviewDecision.Unreviewed);
        var reviewed = rows.Count - unreviewed;
        double? precision = truePositive + falsePositive == 0
            ? null
            : truePositive / (double)(truePositive + falsePositive);
        return Task.FromResult(new PlcMlEvaluationSummary
        {
            ProfileId = profileId,
            ModelVersion = string.IsNullOrWhiteSpace(modelVersion) ? null : modelVersion,
            TotalCandidates = rows.Count,
            ReviewedCandidates = reviewed,
            TruePositives = truePositive,
            FalsePositives = falsePositive,
            ExpectedBehaviors = expected,
            NeedsInvestigation = investigation,
            Unreviewed = unreviewed,
            Precision = precision
        });
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private static PlcMlCandidateEntity ToEntity(PlcMlCandidateRecord value) => new()
    {
        CandidateId = value.CandidateId,
        CandidateKey = value.CandidateKey,
        ProfileId = value.ProfileId,
        ModelVersion = value.ModelVersion,
        DeploymentMode = (int)value.DeploymentMode,
        RoutedToActiveLifecycle = value.RoutedToActiveLifecycle,
        PlcName = value.PlcName,
        DeviceId = value.DeviceId,
        WindowStartUtc = value.WindowStartUtc,
        WindowEndUtc = value.WindowEndUtc,
        Score = value.Score,
        Threshold = value.Threshold,
        Explanation = value.Explanation,
        ContextJson = value.ContextJson,
        IsActive = value.IsActive,
        DetectedUtc = value.DetectedUtc,
        RecoveredUtc = value.RecoveredUtc,
        ReviewDecision = (int)value.ReviewDecision,
        ReviewedBy = value.ReviewedBy,
        ReviewedUtc = value.ReviewedUtc,
        ReviewComment = value.ReviewComment
    };

    private static PlcMlCandidateRecord ToRecord(PlcMlCandidateEntity value) => new()
    {
        CandidateId = value.CandidateId,
        CandidateKey = value.CandidateKey,
        ProfileId = value.ProfileId,
        ModelVersion = value.ModelVersion,
        DeploymentMode = (PlcMlDeploymentMode)value.DeploymentMode,
        RoutedToActiveLifecycle = value.RoutedToActiveLifecycle,
        PlcName = value.PlcName,
        DeviceId = value.DeviceId,
        WindowStartUtc = value.WindowStartUtc,
        WindowEndUtc = value.WindowEndUtc,
        Score = value.Score,
        Threshold = value.Threshold,
        Explanation = value.Explanation,
        ContextJson = value.ContextJson ?? "{}",
        IsActive = value.IsActive,
        DetectedUtc = value.DetectedUtc,
        RecoveredUtc = value.RecoveredUtc,
        ReviewDecision = (PlcMlReviewDecision)value.ReviewDecision,
        ReviewedBy = value.ReviewedBy,
        ReviewedUtc = value.ReviewedUtc,
        ReviewComment = value.ReviewComment
    };

    private static PlcMlModelGovernanceEntity ToEntity(PlcMlModelGovernanceInfo value) => new()
    {
        GovernanceId = value.GovernanceId,
        ProfileId = value.ProfileId,
        ModelVersion = value.ModelVersion,
        DatasetVersion = value.DatasetVersion,
        ApprovalStatus = (int)value.ApprovalStatus,
        RequestedUtc = value.RequestedUtc,
        RequestedBy = value.RequestedBy,
        DecidedUtc = value.DecidedUtc,
        DecidedBy = value.DecidedBy,
        DecisionComment = value.DecisionComment,
        TrainingSampleCount = value.TrainingSampleCount,
        CalibrationSampleCount = value.CalibrationSampleCount,
        DecisionThreshold = value.DecisionThreshold
    };

    private static PlcMlModelGovernanceInfo ToInfo(PlcMlModelGovernanceEntity value) => new()
    {
        GovernanceId = value.GovernanceId,
        ProfileId = value.ProfileId,
        ModelVersion = value.ModelVersion,
        DatasetVersion = value.DatasetVersion,
        ApprovalStatus = (PlcMlApprovalStatus)value.ApprovalStatus,
        RequestedUtc = value.RequestedUtc,
        RequestedBy = value.RequestedBy,
        DecidedUtc = value.DecidedUtc,
        DecidedBy = value.DecidedBy,
        DecisionComment = value.DecisionComment,
        TrainingSampleCount = value.TrainingSampleCount,
        CalibrationSampleCount = value.CalibrationSampleCount,
        DecisionThreshold = value.DecisionThreshold
    };

    private static PlcMlDriftSnapshotEntity ToEntity(PlcMlDriftSnapshot value) => new()
    {
        SnapshotId = value.SnapshotId,
        ProfileId = value.ProfileId,
        ModelVersion = value.ModelVersion,
        CalculatedUtc = value.CalculatedUtc,
        SampleCount = value.SampleCount,
        MeanScore = value.MeanScore,
        P95Score = value.P95Score,
        BaselineMeanScore = value.BaselineMeanScore,
        BaselineP95Score = value.BaselineP95Score,
        DriftRatio = value.DriftRatio,
        Status = (int)value.Status
    };

    private static PlcMlDriftSnapshot ToSnapshot(PlcMlDriftSnapshotEntity value) => new()
    {
        SnapshotId = value.SnapshotId,
        ProfileId = value.ProfileId,
        ModelVersion = value.ModelVersion,
        CalculatedUtc = value.CalculatedUtc,
        SampleCount = value.SampleCount,
        MeanScore = value.MeanScore,
        P95Score = value.P95Score,
        BaselineMeanScore = value.BaselineMeanScore,
        BaselineP95Score = value.BaselineP95Score,
        DriftRatio = value.DriftRatio,
        Status = (PlcMlDriftStatus)value.Status
    };
}
