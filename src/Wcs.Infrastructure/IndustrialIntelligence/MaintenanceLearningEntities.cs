namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;

[SugarTable("Wcs_MaintenanceIntervention")]
public sealed class MaintenanceInterventionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string InterventionId { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string AssetId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string AssetType { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    [SugarColumn(Length = 80, IsNullable = false)] public string PreFeatureSnapshotId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = true)] public string? PostFeatureSnapshotId { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string ActionType { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    [SugarColumn(Length = 200, IsNullable = false)] public string Actor { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string CorrelationId { get; set; } = string.Empty;
}

[SugarTable("Wcs_MaintenanceLearningOutcome")]
public sealed class MaintenanceLearningOutcomeEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string OutcomeId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string InterventionId { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
    public bool FailureObserved { get; set; }
    public decimal DowntimeMinutes { get; set; }
    public decimal ActualCost { get; set; }
    [SugarColumn(Length = 120, IsNullable = true)] public string? FailureCode { get; set; }
    [SugarColumn(Length = 160, IsNullable = false)] public string SourceEventId { get; set; } = string.Empty;
}

[SugarTable("Wcs_MaintenanceEffectiveness")]
public sealed class MaintenanceEffectivenessEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string InterventionId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string EvaluationWindowVersion { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string Status { get; set; } = string.Empty;
    public DateTime EvaluatedAtUtc { get; set; }
    public decimal? DowntimeDeltaMinutes { get; set; }
    public decimal? CostDelta { get; set; }
    public bool? FailureAvoided { get; set; }
    [SugarColumn(Length = 1000, IsNullable = false)] public string Reason { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
}

[SugarTable("Wcs_MaintenanceEvaluationWindow")]
public sealed class MaintenanceEvaluationWindowEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 80, IsNullable = false)] public string AssetType { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string Version { get; set; } = string.Empty;
    public long ImmediateTicks { get; set; }
    public long ShortTicks { get; set; }
    public long MediumTicks { get; set; }
    public long LongTicks { get; set; }
    [SugarColumn(Length = 200, IsNullable = false)] public string ApprovedBy { get; set; } = string.Empty;
    public DateTime ApprovedAtUtc { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string DefinitionHash { get; set; } = string.Empty;
}

[SugarTable("Wcs_MaintenanceCausalCandidate")]
public sealed class MaintenanceCausalCandidateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string CandidateId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string InterventionId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string Treatment { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string OutcomeMetric { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
}

[SugarTable("Wcs_MaintenanceCounterfactualEstimate")]
public sealed class MaintenanceCounterfactualEstimateEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string CandidateId { get; set; } = string.Empty;
    public decimal ObservedValue { get; set; }
    public decimal CounterfactualValue { get; set; }
    public decimal EstimatedEffect { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string MethodVersion { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
}

[SugarTable("Wcs_MaintenanceTrainingLabel")]
public sealed class MaintenanceTrainingLabelEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string LabelId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string InterventionId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string DatasetKey { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string Label { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string State { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

[SugarTable("Wcs_MaintenanceTrainingLabelApproval")]
public sealed class MaintenanceTrainingLabelApprovalEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string LabelId { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string State { get; set; } = string.Empty;
    [SugarColumn(Length = 200, IsNullable = false)] public string Actor { get; set; } = string.Empty;
    [SugarColumn(Length = 2000, IsNullable = false)] public string Reason { get; set; } = string.Empty;
    public DateTime DecidedAtUtc { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string CorrelationId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string IdempotencyKey { get; set; } = string.Empty;
}

[SugarTable("Wcs_MaintenanceMesOutbox")]
public sealed class MaintenanceMesOutboxEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string OutboxId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string InterventionId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string IdempotencyKey { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string PayloadHash { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    [SugarColumn(Length = 2000, IsNullable = true)] public string? LastError { get; set; }
}

public static class MaintenanceLearningSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(
            typeof(MaintenanceInterventionEntity), typeof(MaintenanceLearningOutcomeEntity),
            typeof(MaintenanceEffectivenessEntity), typeof(MaintenanceEvaluationWindowEntity),
            typeof(MaintenanceCausalCandidateEntity), typeof(MaintenanceCounterfactualEstimateEntity),
            typeof(MaintenanceTrainingLabelEntity), typeof(MaintenanceTrainingLabelApprovalEntity),
            typeof(MaintenanceMesOutboxEntity));
        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceIntervention_Id' AND object_id=OBJECT_ID('Wcs_MaintenanceIntervention')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceIntervention_Id ON Wcs_MaintenanceIntervention(InterventionId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceOutcome_Id' AND object_id=OBJECT_ID('Wcs_MaintenanceLearningOutcome')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceOutcome_Id ON Wcs_MaintenanceLearningOutcome(OutcomeId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceOutcome_Source' AND object_id=OBJECT_ID('Wcs_MaintenanceLearningOutcome')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceOutcome_Source ON Wcs_MaintenanceLearningOutcome(SourceEventId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceEffectiveness_InterventionWindow' AND object_id=OBJECT_ID('Wcs_MaintenanceEffectiveness')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceEffectiveness_InterventionWindow ON Wcs_MaintenanceEffectiveness(InterventionId,EvaluationWindowVersion);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceWindow_AssetVersion' AND object_id=OBJECT_ID('Wcs_MaintenanceEvaluationWindow')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceWindow_AssetVersion ON Wcs_MaintenanceEvaluationWindow(AssetType,Version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceCausal_Id' AND object_id=OBJECT_ID('Wcs_MaintenanceCausalCandidate')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceCausal_Id ON Wcs_MaintenanceCausalCandidate(CandidateId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceCounterfactual_Candidate' AND object_id=OBJECT_ID('Wcs_MaintenanceCounterfactualEstimate')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceCounterfactual_Candidate ON Wcs_MaintenanceCounterfactualEstimate(CandidateId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceLabel_Id' AND object_id=OBJECT_ID('Wcs_MaintenanceTrainingLabel')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceLabel_Id ON Wcs_MaintenanceTrainingLabel(LabelId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceLabelApproval_Idempotency' AND object_id=OBJECT_ID('Wcs_MaintenanceTrainingLabelApproval')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceLabelApproval_Idempotency ON Wcs_MaintenanceTrainingLabelApproval(IdempotencyKey);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_MaintenanceOutbox_Idempotency' AND object_id=OBJECT_ID('Wcs_MaintenanceMesOutbox')) CREATE UNIQUE INDEX UX_Wcs_MaintenanceOutbox_Idempotency ON Wcs_MaintenanceMesOutbox(IdempotencyKey);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Wcs_MaintenanceOutbox_Pending' AND object_id=OBJECT_ID('Wcs_MaintenanceMesOutbox')) CREATE INDEX IX_Wcs_MaintenanceOutbox_Pending ON Wcs_MaintenanceMesOutbox(DeliveredAtUtc,CreatedAtUtc);");
    }
}
