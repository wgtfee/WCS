namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;

[SugarTable("Wcs_IndustrialIntelligenceAuditJournal")]
public sealed class IndustrialIntelligenceAuditJournalEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 80, IsNullable = false)]
    public string AuditId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string TargetType { get; set; } = string.Empty;

    [SugarColumn(Length = 200, IsNullable = false)]
    public string TargetId { get; set; } = string.Empty;

    [SugarColumn(Length = 200, IsNullable = false)]
    public string Actor { get; set; } = string.Empty;

    [SugarColumn(Length = 2000, IsNullable = false)]
    public string Reason { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime OccurredAtUtc { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string CorrelationId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? PayloadHash { get; set; }
}

[SugarTable("Wcs_IndustrialIntelligenceEvidence")]
public sealed class IndustrialIntelligenceEvidenceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 80, IsNullable = false)]
    public string EvidenceId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string EvidenceType { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string SubjectType { get; set; } = string.Empty;

    [SugarColumn(Length = 200, IsNullable = false)]
    public string SubjectId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string Version { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string Sha256 { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(Length = 200, IsNullable = false)]
    public string CreatedBy { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string CorrelationId { get; set; } = string.Empty;
}

public static class IndustrialIntelligenceGovernanceSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(
            typeof(IndustrialIntelligenceAuditJournalEntity),
            typeof(IndustrialIntelligenceEvidenceEntity));

        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_IndustrialIntelligenceAudit_AuditId' AND object_id = OBJECT_ID('Wcs_IndustrialIntelligenceAuditJournal'))
    CREATE UNIQUE INDEX UX_Wcs_IndustrialIntelligenceAudit_AuditId ON Wcs_IndustrialIntelligenceAuditJournal(AuditId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_IndustrialIntelligenceAudit_Correlation' AND object_id = OBJECT_ID('Wcs_IndustrialIntelligenceAuditJournal'))
    CREATE INDEX IX_Wcs_IndustrialIntelligenceAudit_Correlation ON Wcs_IndustrialIntelligenceAuditJournal(CorrelationId, OccurredAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_IndustrialIntelligenceEvidence_EvidenceId' AND object_id = OBJECT_ID('Wcs_IndustrialIntelligenceEvidence'))
    CREATE UNIQUE INDEX UX_Wcs_IndustrialIntelligenceEvidence_EvidenceId ON Wcs_IndustrialIntelligenceEvidence(EvidenceId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_IndustrialIntelligenceEvidence_Subject' AND object_id = OBJECT_ID('Wcs_IndustrialIntelligenceEvidence'))
    CREATE INDEX IX_Wcs_IndustrialIntelligenceEvidence_Subject ON Wcs_IndustrialIntelligenceEvidence(SubjectType, SubjectId, CreatedAtUtc DESC);");
    }
}
