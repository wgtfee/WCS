namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;

[SugarTable("Wcs_OptimizationExperiment")]
public sealed class OptimizationExperimentEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string ExperimentId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string DefinitionHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string SoftwareHead { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string DefinitionJson { get; set; } = string.Empty;
    [SugarColumn(Length = 32, IsNullable = false)] public string Status { get; set; } = "Defined";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

[SugarTable("Wcs_OptimizationExperimentResult")]
public sealed class OptimizationExperimentResultEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string ExperimentId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string DefinitionHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string SoftwareHead { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string ResultJson { get; set; } = string.Empty;
    public bool ControlWriteAllowed { get; set; }
    public bool AutoProductionPolicyReplacementAllowed { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

[SugarTable("Wcs_OptimizationPolicyEvidence")]
public sealed class OptimizationPolicyEvidenceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string ExperimentId { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string PolicyId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string PolicyHash { get; set; } = string.Empty;
    public double Score { get; set; }
    public bool ParetoEfficient { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }
}

public static class OptimizationSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(typeof(OptimizationExperimentEntity), typeof(OptimizationExperimentResultEntity), typeof(OptimizationPolicyEvidenceEntity));
        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationExperiment_Id' AND object_id=OBJECT_ID('Wcs_OptimizationExperiment')) CREATE UNIQUE INDEX UX_Wcs_OptimizationExperiment_Id ON Wcs_OptimizationExperiment(ExperimentId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationExperiment_DefinitionHash' AND object_id=OBJECT_ID('Wcs_OptimizationExperiment')) CREATE UNIQUE INDEX UX_Wcs_OptimizationExperiment_DefinitionHash ON Wcs_OptimizationExperiment(DefinitionHash);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationResult_Experiment' AND object_id=OBJECT_ID('Wcs_OptimizationExperimentResult')) CREATE UNIQUE INDEX UX_Wcs_OptimizationResult_Experiment ON Wcs_OptimizationExperimentResult(ExperimentId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationPolicyEvidence' AND object_id=OBJECT_ID('Wcs_OptimizationPolicyEvidence')) CREATE UNIQUE INDEX UX_Wcs_OptimizationPolicyEvidence ON Wcs_OptimizationPolicyEvidence(ExperimentId,PolicyId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Wcs_OptimizationExperiment_Created' AND object_id=OBJECT_ID('Wcs_OptimizationExperiment')) CREATE INDEX IX_Wcs_OptimizationExperiment_Created ON Wcs_OptimizationExperiment(CreatedAtUtc DESC);");
    }
}
