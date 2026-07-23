namespace Wcs.Core.AnomalyDetection;

using System.Text.Json;

/// <summary>PLC 异常检测器类型。</summary>
public enum PlcAnomalyType
{
    Threshold = 0,
    RateOfChange = 1,
    Duration = 2,
    StatisticalBaseline = 3,
    Consistency = 4
}

/// <summary>异常严重级别。Observe 只记录，不进入报警中心。</summary>
public enum PlcAnomalySeverity
{
    Observe = 0,
    Warning = 1,
    Error = 2,
    Critical = 3
}

public enum PlcAnomalyLifecycleStatus
{
    Active = 0,
    Recovered = 1
}

/// <summary>单条信号的异常检测规则。</summary>
public sealed class PlcAnomalyRule
{
    public string RuleId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string PlcPattern { get; set; } = "*";
    public string DevicePattern { get; set; } = "*";
    public string SignalPattern { get; set; } = string.Empty;

    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public double? MaximumRatePerSecond { get; set; }
    public int? MaximumTrueDurationMs { get; set; }

    public bool StatisticalBaselineEnabled { get; set; }
    public double MadMultiplier { get; set; } = 6.0;
    public double MinimumMad { get; set; } = 0.001;

    public PlcAnomalySeverity Severity { get; set; } = PlcAnomalySeverity.Warning;
    public bool RaiseAlarm { get; set; } = true;
    public int? ConsecutiveAbnormalCount { get; set; }
    public int? ConsecutiveRecoveryCount { get; set; }
    public string? Description { get; set; }
}

/// <summary>异常检测运行参数。</summary>
public sealed class PlcAnomalyOptions
{
    public bool Enabled { get; set; }
    public int WindowSize { get; set; } = 120;
    public int MinimumSamples { get; set; } = 30;
    public int MaximumTrackedRuleSignals { get; set; } = 20_000;

    public double ObserveThreshold { get; set; } = 0.70;
    public double WarningThreshold { get; set; } = 0.85;
    public double AlarmThreshold { get; set; } = 0.95;
    public int ConsecutiveWarningCount { get; set; } = 3;
    public int ConsecutiveAlarmCount { get; set; } = 5;
    public int RecoveryCount { get; set; } = 10;
    public int DurationSweepIntervalMs { get; set; } = 1_000;

    public int AlarmDelayRaiseMs { get; set; }
    public int AlarmDelayRecoverMs { get; set; } = 1_000;
    public List<PlcAnomalyRule> Rules { get; set; } = new();
}

/// <summary>由 RawSignalEvent 转换而来的数据库无关检测样本。</summary>
public sealed record PlcAnomalySample
{
    public required string EventId { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string PlcName { get; init; }
    public required int DbBlock { get; init; }
    public required string DeviceId { get; init; }
    public required string SignalName { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public double? NumericValue { get; init; }
    public bool? BooleanValue { get; init; }
    public string Source { get; init; } = "PLC";
    public string? TaskId { get; init; }
}

/// <summary>异常生命周期实体，在检测、报警、SQL 与查询接口之间传递。</summary>
public sealed record PlcAnomalyRecord
{
    public required string AnomalyId { get; init; }
    public required string AnomalyKey { get; init; }
    public required string AlarmCode { get; init; }
    public required string RuleId { get; init; }
    public required PlcAnomalyType Type { get; init; }
    public required PlcAnomalySeverity Severity { get; init; }
    public required PlcAnomalyLifecycleStatus Status { get; init; }
    public required string PlcName { get; init; }
    public required int DbBlock { get; init; }
    public required string DeviceId { get; init; }
    public required string SignalName { get; init; }
    public required string DetectorName { get; init; }
    public required string ModelVersion { get; init; }
    public required double Score { get; init; }
    public double? ActualValue { get; init; }
    public double? ExpectedValue { get; init; }
    public double? LowerBound { get; init; }
    public double? UpperBound { get; init; }
    public required DateTime StartTimeUtc { get; init; }
    public required DateTime LastSeenUtc { get; init; }
    public DateTime? EndTimeUtc { get; init; }
    public required string Reason { get; init; }
    public string? TaskId { get; init; }
    public bool RaiseAlarm { get; init; }
    public string ContextJson { get; init; } = "{}";

    public PlcAnomalyRecord Recover(DateTime recoveredUtc) => this with
    {
        Status = PlcAnomalyLifecycleStatus.Recovered,
        LastSeenUtc = recoveredUtc,
        EndTimeUtc = recoveredUtc
    };

    public static string SerializeContext(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

public sealed record PlcAnomalyStatus
{
    public bool Enabled { get; init; }
    public int ConfiguredRules { get; init; }
    public long ProcessedSamples { get; init; }
    public long MatchedRuleEvaluations { get; init; }
    public long DetectorObservations { get; init; }
    public long Raised { get; init; }
    public long Recovered { get; init; }
    public long Suppressed { get; init; }
    public long Failures { get; init; }
    public int TrackedRuleSignals { get; init; }
    public int ActiveAnomalies { get; init; }
    public DateTime? LastProcessedUtc { get; init; }
    public string? LastError { get; init; }
}

public interface IPlcAnomalyEngine
{
    ValueTask ProcessAsync(PlcAnomalySample sample, CancellationToken cancellationToken = default);
    ValueTask SweepAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    IReadOnlyList<PlcAnomalyRecord> GetActiveAnomalies();
}

public interface IPlcAnomalyStatusProvider
{
    PlcAnomalyStatus GetStatus();
}
