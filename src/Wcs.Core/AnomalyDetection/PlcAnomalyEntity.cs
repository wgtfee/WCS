namespace Wcs.Core.AnomalyDetection;

using SqlSugar;

/// <summary>PLC 异常生命周期业务表。</summary>
[SugarTable("Wcs_PlcAnomaly")]
public sealed class PlcAnomalyEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 40)]
    public string AnomalyId { get; set; } = string.Empty;

    [SugarColumn(Length = 220)]
    public string AnomalyKey { get; set; } = string.Empty;

    [SugarColumn(Length = 80)]
    public string AlarmCode { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string RuleId { get; set; } = string.Empty;

    public int Type { get; set; }
    public int Severity { get; set; }
    public int Status { get; set; }

    [SugarColumn(Length = 100)]
    public string PlcName { get; set; } = string.Empty;

    public int DbBlock { get; set; }

    [SugarColumn(Length = 200)]
    public string DeviceId { get; set; } = string.Empty;

    [SugarColumn(Length = 300)]
    public string SignalName { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string DetectorName { get; set; } = string.Empty;

    [SugarColumn(Length = 40)]
    public string ModelVersion { get; set; } = string.Empty;

    public double Score { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? ActualValue { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? ExpectedValue { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? LowerBound { get; set; }

    [SugarColumn(IsNullable = true)]
    public double? UpperBound { get; set; }

    public DateTime StartTimeUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? EndTimeUtc { get; set; }

    [SugarColumn(Length = 2000)]
    public string Reason { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? TaskId { get; set; }

    public bool RaiseAlarm { get; set; }

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = true)]
    public string? ContextJson { get; set; }
}
