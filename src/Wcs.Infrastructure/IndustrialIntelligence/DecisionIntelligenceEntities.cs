namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;

[SugarTable("Wcs_DecisionProposal")]
public sealed class DecisionProposalEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string ProposalId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string ProposalType { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string CorrelationId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string IdempotencyKey { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string ProposalJson { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string ProposalHash { get; set; } = string.Empty;
}

[SugarTable("Wcs_DecisionConstraintResult")]
public sealed class DecisionConstraintResultEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string ProposalId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string Code { get; set; } = string.Empty;
    public bool Passed { get; set; }
    [SugarColumn(Length = 2000, IsNullable = false)] public string Reason { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}

[SugarTable("Wcs_DecisionApprovalJournal")]
public sealed class DecisionApprovalJournalEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string ProposalId { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string FromStatus { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string ToStatus { get; set; } = string.Empty;
    [SugarColumn(Length = 200, IsNullable = false)] public string Actor { get; set; } = string.Empty;
    [SugarColumn(Length = 2000, IsNullable = false)] public string Reason { get; set; } = string.Empty;
    public DateTime Utc { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string CorrelationId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string IdempotencyKey { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EntryHash { get; set; } = string.Empty;
}

[SugarTable("Wcs_DecisionOutcomeJournal")]
public sealed class DecisionOutcomeJournalEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string ProposalId { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string OutcomeType { get; set; } = string.Empty;
    [SugarColumn(Length = 500, IsNullable = false)] public string ActualReference { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)] public decimal? ActualBenefit { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
}

[SugarTable("Wcs_DecisionExplanationEvidence")]
public sealed class DecisionExplanationEvidenceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string ProposalId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string ModelId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string ModelVersion { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string FeatureSnapshotId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string FeatureSchemaHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string ModelEvidenceHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string ExplanationEvidenceHash { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string ExplanationJson { get; set; } = string.Empty;
}

public static class DecisionIntelligenceSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(typeof(DecisionProposalEntity), typeof(DecisionConstraintResultEntity),
            typeof(DecisionApprovalJournalEntity), typeof(DecisionOutcomeJournalEntity), typeof(DecisionExplanationEvidenceEntity));
        db.Ado.ExecuteCommand(@"
IF OBJECT_ID('dbo.Wcs_DecisionOutcomeJournal') IS NOT NULL
   AND EXISTS (
       SELECT 1
       FROM sys.columns
       WHERE object_id = OBJECT_ID('dbo.Wcs_DecisionOutcomeJournal')
         AND name = 'ActualBenefit'
         AND is_nullable = 0)
BEGIN
    DECLARE @ActualBenefitPrecision int;
    DECLARE @ActualBenefitScale int;
    SELECT
        @ActualBenefitPrecision = precision,
        @ActualBenefitScale = scale
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Wcs_DecisionOutcomeJournal')
      AND name = 'ActualBenefit';

    DECLARE @AlterActualBenefit nvarchar(400) =
        N'ALTER TABLE dbo.Wcs_DecisionOutcomeJournal ALTER COLUMN ActualBenefit decimal('
        + CONVERT(nvarchar(10), @ActualBenefitPrecision)
        + N',' + CONVERT(nvarchar(10), @ActualBenefitScale) + N') NULL';
    EXEC sp_executesql @AlterActualBenefit;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_DecisionProposal_Id' AND object_id=OBJECT_ID('Wcs_DecisionProposal')) CREATE UNIQUE INDEX UX_Wcs_DecisionProposal_Id ON Wcs_DecisionProposal(ProposalId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_DecisionProposal_Idempotency' AND object_id=OBJECT_ID('Wcs_DecisionProposal')) CREATE UNIQUE INDEX UX_Wcs_DecisionProposal_Idempotency ON Wcs_DecisionProposal(IdempotencyKey);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Wcs_DecisionProposal_StatusCreated' AND object_id=OBJECT_ID('Wcs_DecisionProposal')) CREATE INDEX IX_Wcs_DecisionProposal_StatusCreated ON Wcs_DecisionProposal(Status,CreatedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_DecisionConstraint_Ordinal' AND object_id=OBJECT_ID('Wcs_DecisionConstraintResult')) CREATE UNIQUE INDEX UX_Wcs_DecisionConstraint_Ordinal ON Wcs_DecisionConstraintResult(ProposalId,Ordinal);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_DecisionApproval_Idempotency' AND object_id=OBJECT_ID('Wcs_DecisionApprovalJournal')) CREATE UNIQUE INDEX UX_Wcs_DecisionApproval_Idempotency ON Wcs_DecisionApprovalJournal(IdempotencyKey);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_DecisionOutcome_Proposal' AND object_id=OBJECT_ID('Wcs_DecisionOutcomeJournal')) CREATE UNIQUE INDEX UX_Wcs_DecisionOutcome_Proposal ON Wcs_DecisionOutcomeJournal(ProposalId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_DecisionExplanation_Proposal' AND object_id=OBJECT_ID('Wcs_DecisionExplanationEvidence')) CREATE UNIQUE INDEX UX_Wcs_DecisionExplanation_Proposal ON Wcs_DecisionExplanationEvidence(ProposalId);");
    }
}
