namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;

[SugarTable("Wcs_FeatureDefinition")]
public sealed class FeatureDefinitionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 160, IsNullable = false)] public string FeatureId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string Version { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string DefinitionHash { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string DefinitionJson { get; set; } = string.Empty;
    [SugarColumn(IsNullable = false)] public DateTime CreatedAtUtc { get; set; }
}

[SugarTable("Wcs_FeatureSchema")]
public sealed class FeatureSchemaEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 160, IsNullable = false)] public string SchemaId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string Version { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string SchemaHash { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string Status { get; set; } = string.Empty;
    [SugarColumn(Length = 200, IsNullable = false)] public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
}

[SugarTable("Wcs_FeatureSchemaItem")]
public sealed class FeatureSchemaItemEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 160, IsNullable = false)] public string SchemaId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string SchemaVersion { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string FeatureId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string DefinitionHash { get; set; } = string.Empty;
    public int Ordinal { get; set; }
}

[SugarTable("Wcs_FeatureSnapshot")]
public sealed class FeatureSnapshotEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string SnapshotId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string EntityId { get; set; } = string.Empty;
    public DateTime AsOfUtc { get; set; }
    [SugarColumn(Length = 160, IsNullable = false)] public string FeatureSchemaId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string FeatureSchemaHash { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string ValuesJson { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)] public string SourceOffsetsJson { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string ValuesHash { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string QualityStatus { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string MaterializerVersion { get; set; } = string.Empty;
}

[SugarTable("Wcs_FeatureQualityEvent")]
public sealed class FeatureQualityEventEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 80, IsNullable = false)] public string QualityEventId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string EntityId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string FeatureId { get; set; } = string.Empty;
    [SugarColumn(Length = 40, IsNullable = false)] public string Status { get; set; } = string.Empty;
    [SugarColumn(Length = 2000, IsNullable = false)] public string Reason { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string EvidenceSha256 { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string CorrelationId { get; set; } = string.Empty;
}

[SugarTable("Wcs_FeatureDataset")]
public sealed class FeatureDatasetEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 160, IsNullable = false)] public string DatasetId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string Version { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string FeatureSchemaId { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string FeatureSchemaHash { get; set; } = string.Empty;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public long RowCount { get; set; }
    [SugarColumn(Length = 64, IsNullable = false)] public string DatasetHash { get; set; } = string.Empty;
    [SugarColumn(Length = 1000, IsNullable = false)] public string StorageUri { get; set; } = string.Empty;
    [SugarColumn(Length = 64, IsNullable = false)] public string StorageSha256 { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    [SugarColumn(Length = 200, IsNullable = false)] public string CreatedBy { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string CorrelationId { get; set; } = string.Empty;
}

[SugarTable("Wcs_FeatureLineage")]
public sealed class FeatureLineageEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public long Id { get; set; }
    [SugarColumn(Length = 80, IsNullable = false)] public string LineageId { get; set; } = string.Empty;
    [SugarColumn(Length = 160, IsNullable = false)] public string OutputId { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string OutputType { get; set; } = string.Empty;
    [SugarColumn(Length = 80, IsNullable = false)] public string SourceType { get; set; } = string.Empty;
    [SugarColumn(Length = 200, IsNullable = false)] public string SourceId { get; set; } = string.Empty;
    public DateTime AsOfUtc { get; set; }
    [SugarColumn(Length = 120, IsNullable = false)] public string TransformationVersion { get; set; } = string.Empty;
    [SugarColumn(Length = 120, IsNullable = false)] public string CorrelationId { get; set; } = string.Empty;
}

public static class FeatureCenterSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(typeof(FeatureDefinitionEntity), typeof(FeatureSchemaEntity), typeof(FeatureSchemaItemEntity),
            typeof(FeatureSnapshotEntity), typeof(FeatureQualityEventEntity), typeof(FeatureDatasetEntity), typeof(FeatureLineageEntity));
        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_FeatureDefinition_Version' AND object_id=OBJECT_ID('Wcs_FeatureDefinition')) CREATE UNIQUE INDEX UX_Wcs_FeatureDefinition_Version ON Wcs_FeatureDefinition(FeatureId,Version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_FeatureSchema_Version' AND object_id=OBJECT_ID('Wcs_FeatureSchema')) CREATE UNIQUE INDEX UX_Wcs_FeatureSchema_Version ON Wcs_FeatureSchema(SchemaId,Version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_FeatureSchemaItem_Ordinal' AND object_id=OBJECT_ID('Wcs_FeatureSchemaItem')) CREATE UNIQUE INDEX UX_Wcs_FeatureSchemaItem_Ordinal ON Wcs_FeatureSchemaItem(SchemaId,SchemaVersion,Ordinal);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_FeatureSnapshot_Id' AND object_id=OBJECT_ID('Wcs_FeatureSnapshot')) CREATE UNIQUE INDEX UX_Wcs_FeatureSnapshot_Id ON Wcs_FeatureSnapshot(SnapshotId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_Wcs_FeatureSnapshot_EntityAsOf' AND object_id=OBJECT_ID('Wcs_FeatureSnapshot')) CREATE INDEX IX_Wcs_FeatureSnapshot_EntityAsOf ON Wcs_FeatureSnapshot(EntityId,AsOfUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_FeatureQualityEvent_Id' AND object_id=OBJECT_ID('Wcs_FeatureQualityEvent')) CREATE UNIQUE INDEX UX_Wcs_FeatureQualityEvent_Id ON Wcs_FeatureQualityEvent(QualityEventId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_FeatureDataset_Version' AND object_id=OBJECT_ID('Wcs_FeatureDataset')) CREATE UNIQUE INDEX UX_Wcs_FeatureDataset_Version ON Wcs_FeatureDataset(DatasetId,Version);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UX_Wcs_FeatureLineage_Id' AND object_id=OBJECT_ID('Wcs_FeatureLineage')) CREATE UNIQUE INDEX UX_Wcs_FeatureLineage_Id ON Wcs_FeatureLineage(LineageId);");
    }
}
