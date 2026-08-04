namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;

[SugarTable("Wcs_AiModelRegistry")]
public sealed class AiModelRegistryEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelType { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string ManifestHash { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string LifecycleStatus { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(Length = 200, IsNullable = false)]
    public string CreatedBy { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string CorrelationId { get; set; } = string.Empty;
}

[SugarTable("Wcs_AiModelPackage")]
public sealed class AiModelPackageEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 500, IsNullable = false)]
    public string ArtifactFile { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string ArtifactSha256 { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string FeatureSchemaId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string FeatureSchemaHash { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string TrainingDatasetVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string TrainingDatasetHash { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public long PackageBytes { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime ValidatedAtUtc { get; set; }
}

[SugarTable("Wcs_AiModelDeployment")]
public sealed class AiModelDeploymentEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string AssetType { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string Profile { get; set; } = string.Empty;

    [SugarColumn(Length = 40, IsNullable = false)]
    public string DeploymentStatus { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(Length = 200, IsNullable = false)]
    public string Actor { get; set; } = string.Empty;

    [SugarColumn(Length = 2000, IsNullable = false)]
    public string Reason { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string CorrelationId { get; set; } = string.Empty;
}

[SugarTable("Wcs_AiModelEvaluation")]
public sealed class AiModelEvaluationEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 80, IsNullable = false)]
    public string EvaluationId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string DatasetVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string DatasetHash { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string MetricsJson { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string EvidenceSha256 { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string CorrelationId { get; set; } = string.Empty;
}

[SugarTable("Wcs_AiModelDriftEvent")]
public sealed class AiModelDriftEventEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 80, IsNullable = false)]
    public string DriftEventId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string DriftKind { get; set; } = string.Empty;

    public double ObservedValue { get; set; }
    public double Threshold { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime OccurredAtUtc { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string EvidenceSha256 { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string CorrelationId { get; set; } = string.Empty;
}

[SugarTable("Wcs_AiModelAuditJournal")]
public sealed class AiModelAuditJournalEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 80, IsNullable = false)]
    public string AuditId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string Action { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string ModelVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 200, IsNullable = false)]
    public string Actor { get; set; } = string.Empty;

    [SugarColumn(Length = 2000, IsNullable = false)]
    public string Reason { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime OccurredAtUtc { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string CorrelationId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string PayloadHash { get; set; } = string.Empty;
}

public static class ModelOpsSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(
            typeof(AiModelRegistryEntity),
            typeof(AiModelPackageEntity),
            typeof(AiModelDeploymentEntity),
            typeof(AiModelEvaluationEntity),
            typeof(AiModelDriftEventEntity),
            typeof(AiModelAuditJournalEntity));

        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AiModelRegistry_ModelVersion' AND object_id = OBJECT_ID('Wcs_AiModelRegistry'))
    CREATE UNIQUE INDEX UX_Wcs_AiModelRegistry_ModelVersion ON Wcs_AiModelRegistry(ModelId, ModelVersion);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AiModelDeployment_ScopeStatus' AND object_id = OBJECT_ID('Wcs_AiModelDeployment'))
    CREATE INDEX IX_Wcs_AiModelDeployment_ScopeStatus ON Wcs_AiModelDeployment(ModelId, AssetType, Profile, DeploymentStatus);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AiModelRegistry_CreatedAt' AND object_id = OBJECT_ID('Wcs_AiModelRegistry'))
    CREATE INDEX IX_Wcs_AiModelRegistry_CreatedAt ON Wcs_AiModelRegistry(CreatedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AiModelRegistry_Correlation' AND object_id = OBJECT_ID('Wcs_AiModelRegistry'))
    CREATE INDEX IX_Wcs_AiModelRegistry_Correlation ON Wcs_AiModelRegistry(CorrelationId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AiModelEvaluation_EvaluationId' AND object_id = OBJECT_ID('Wcs_AiModelEvaluation'))
    CREATE UNIQUE INDEX UX_Wcs_AiModelEvaluation_EvaluationId ON Wcs_AiModelEvaluation(EvaluationId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AiModelDriftEvent_DriftEventId' AND object_id = OBJECT_ID('Wcs_AiModelDriftEvent'))
    CREATE UNIQUE INDEX UX_Wcs_AiModelDriftEvent_DriftEventId ON Wcs_AiModelDriftEvent(DriftEventId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AiModelAudit_AuditId' AND object_id = OBJECT_ID('Wcs_AiModelAuditJournal'))
    CREATE UNIQUE INDEX UX_Wcs_AiModelAudit_AuditId ON Wcs_AiModelAuditJournal(AuditId);");
    }
}
