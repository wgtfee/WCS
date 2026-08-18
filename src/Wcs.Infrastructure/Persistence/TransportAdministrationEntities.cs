namespace Wcs.Infrastructure.Persistence;

using SqlSugar;

[SugarTable("Wcs_TransportConfiguration")]
public sealed class TransportConfigurationEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 100)]
    public string ConfigurationId { get; set; } = string.Empty;
    public long Version { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string PayloadJson { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true, Length = 200)]
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

[SugarTable("Wcs_TransportJournal")]
public sealed class TransportJournalEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 150)]
    public string JournalKey { get; set; } = string.Empty;
    public int Category { get; set; }
    [SugarColumn(Length = 120)]
    public string RecordId { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

[SugarTable("Wcs_TransportGovernedOperation")]
public sealed class TransportGovernedOperationEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string OperationId { get; set; } = string.Empty;
    public int OperationType { get; set; }
    public int State { get; set; }
    [SugarColumn(Length = 200)]
    public string TargetId { get; set; } = string.Empty;
    [SugarColumn(Length = 200)]
    public string RequestedBy { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

[SugarTable("Wcs_TransportAudit")]
public sealed class TransportAuditEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string AuditId { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string OperationId { get; set; } = string.Empty;
    [SugarColumn(Length = 100)]
    public string Action { get; set; } = string.Empty;
    [SugarColumn(Length = 200)]
    public string ActorId { get; set; } = string.Empty;
    [SugarColumn(Length = 200)]
    public string TargetId { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? PayloadJson { get; set; }
    public bool Success { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}
