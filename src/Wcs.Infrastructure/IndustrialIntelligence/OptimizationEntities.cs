namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;

[SugarTable("Wcs_OptimizationExperiment")]
public sealed class OptimizationExperimentEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string ExperimentId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string DefinitionHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string SoftwareHead { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string ScenarioEvidenceHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string TopologyEvidenceHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string OrderDatasetEvidenceHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string ObjectiveWeightsEvidenceHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string ConstraintProfileHash { get; set; } = string.Empty;
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
    public bool ProductionAutomationAllowed { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

[SugarTable("Wcs_OptimizationPolicyEvidence")]
public sealed class OptimizationPolicyEvidenceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string ExperimentId { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string PolicyId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string PolicyHash { get; set; } = string.Empty;
    public int Rank { get; set; }
    public double Score { get; set; }
    public bool ParetoEfficient { get; set; }
    public bool HardConstraintQualified { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string AggregateJson { get; set; } = string.Empty;
}

[SugarTable("Wcs_OptimizationRunEvidence")]
public sealed class OptimizationRunEvidenceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string ExperimentId { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string PolicyId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string PolicyHash { get; set; } = string.Empty;
    [SugarColumn(Length = 48, IsNullable = false)] public string LoadCase { get; set; } = string.Empty;
    public int Seed { get; set; }
    public int DeterminismRound { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string ScenarioHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string FinalStateHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceHash { get; set; } = string.Empty;
    public bool HardConstraintsSatisfied { get; set; }
    [SugarColumn(Length = 2000, IsNullable = true)] public string? FailureReason { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string MetricsJson { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string StageEvidenceJson { get; set; } = string.Empty;
}

public static class OptimizationSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(
            typeof(OptimizationExperimentEntity),
            typeof(OptimizationExperimentResultEntity),
            typeof(OptimizationPolicyEvidenceEntity),
            typeof(OptimizationRunEvidenceEntity));
        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationExperiment_Id' AND object_id=OBJECT_ID('Wcs_OptimizationExperiment')) CREATE UNIQUE INDEX UX_Wcs_OptimizationExperiment_Id ON Wcs_OptimizationExperiment(ExperimentId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationExperiment_DefinitionHash' AND object_id=OBJECT_ID('Wcs_OptimizationExperiment')) CREATE UNIQUE INDEX UX_Wcs_OptimizationExperiment_DefinitionHash ON Wcs_OptimizationExperiment(DefinitionHash);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationResult_Experiment' AND object_id=OBJECT_ID('Wcs_OptimizationExperimentResult')) CREATE UNIQUE INDEX UX_Wcs_OptimizationResult_Experiment ON Wcs_OptimizationExperimentResult(ExperimentId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationPolicyEvidence' AND object_id=OBJECT_ID('Wcs_OptimizationPolicyEvidence')) CREATE UNIQUE INDEX UX_Wcs_OptimizationPolicyEvidence ON Wcs_OptimizationPolicyEvidence(ExperimentId,PolicyId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_OptimizationRunEvidence' AND object_id=OBJECT_ID('Wcs_OptimizationRunEvidence')) CREATE UNIQUE INDEX UX_Wcs_OptimizationRunEvidence ON Wcs_OptimizationRunEvidence(ExperimentId,PolicyId,LoadCase,Seed,DeterminismRound);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Wcs_OptimizationRunEvidence_Experiment' AND object_id=OBJECT_ID('Wcs_OptimizationRunEvidence')) CREATE INDEX IX_Wcs_OptimizationRunEvidence_Experiment ON Wcs_OptimizationRunEvidence(ExperimentId,PolicyId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Wcs_OptimizationExperiment_Created' AND object_id=OBJECT_ID('Wcs_OptimizationExperiment')) CREATE INDEX IX_Wcs_OptimizationExperiment_Created ON Wcs_OptimizationExperiment(CreatedAtUtc DESC);");
    }
}
