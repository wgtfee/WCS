namespace Wcs.Core.Telemetry;

using SqlSugar;

/// <summary>SQL Server 模式下的 PLC 时序历史表。</summary>
[SugarTable("Wcs_PlcTelemetry")]
public sealed class PlcTelemetryEntity
{
    public long Sequence { get; set; }

    public long TimestampUnixNanoseconds { get; set; }
    public DateTime TimestampUtc { get; set; }

    [SugarColumn(IsPrimaryKey = true, Length = 40)]
    public string EventId { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string Site { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string PlcName { get; set; } = string.Empty;

    public int DbBlock { get; set; }

    [SugarColumn(Length = 200)]
    public string DeviceId { get; set; } = string.Empty;

    [SugarColumn(Length = 300)]
    public string SignalName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 1000)]
    public string? OldValue { get; set; }

    [SugarColumn(IsNullable = true, Length = 1000)]
    public string? NewValue { get; set; }

    public int ValueKind { get; set; }

    [SugarColumn(IsNullable = true)]
    public bool? BoolValue { get; set; }

    [SugarColumn(IsNullable = true)]
    public decimal? NumericValue { get; set; }

    [SugarColumn(IsNullable = true, Length = 2000)]
    public string? TextValue { get; set; }

    public int Quality { get; set; }
    public bool ValidatorPassed { get; set; }

    [SugarColumn(IsNullable = true, Length = 1000)]
    public string? ValidatorReason { get; set; }

    [SugarColumn(IsNullable = true, Length = 300)]
    public string? DomainEventType { get; set; }

    [SugarColumn(Length = 100)]
    public string Source { get; set; } = string.Empty;
}
