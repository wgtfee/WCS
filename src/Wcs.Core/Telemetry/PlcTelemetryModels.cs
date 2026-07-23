namespace Wcs.Core.Telemetry;

/// <summary>PLC 高频历史数据存储类型。</summary>
public enum PlcTelemetryProvider
{
    Disabled = 0,
    SqlServer = 1,
    InfluxDb = 2
}

/// <summary>InfluxDB HTTP API 类型。</summary>
public enum InfluxDbApiVersion
{
    V2 = 2,
    V3 = 3
}

public enum PlcTelemetryValueKind
{
    Boolean = 0,
    Numeric = 1,
    Text = 2
}

/// <summary>
/// PLC 时序存储配置。业务数据库仍由 ConnectionStrings:WcsDb 管理，
/// 本配置仅决定 PLC 高频历史数据保存位置。
/// </summary>
public sealed class PlcTelemetryOptions
{
    public PlcTelemetryProvider Provider { get; set; } = PlcTelemetryProvider.SqlServer;
    public int ChannelCapacity { get; set; } = 100_000;
    public int BatchSize { get; set; } = 1_000;
    public int FlushIntervalMs { get; set; } = 1_000;
    public int RetryDelayMs { get; set; } = 2_000;
    public string SpoolDirectory { get; set; } = "data/plc-telemetry-spool";
    public string Site { get; set; } = "default";
    public string Measurement { get; set; } = "plc_signal";
    public InfluxDbTelemetryOptions InfluxDb { get; set; } = new();
}

public sealed class InfluxDbTelemetryOptions
{
    public InfluxDbApiVersion ApiVersion { get; set; } = InfluxDbApiVersion.V2;
    public string Url { get; set; } = "http://127.0.0.1:8086";
    public string Token { get; set; } = string.Empty;
    public string Organization { get; set; } = "wcs";
    public string Bucket { get; set; } = "wcs_plc";
    public string Database { get; set; } = "wcs_plc";
    public bool Gzip { get; set; }
}

/// <summary>与具体数据库无关的 PLC 时序点。</summary>
public sealed record PlcTelemetryPoint
{
    public required long Sequence { get; init; }
    public required long TimestampUnixNanoseconds { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required string EventId { get; init; }
    public required string Site { get; init; }
    public required string PlcName { get; init; }
    public required int DbBlock { get; init; }
    public required string DeviceId { get; init; }
    public required string SignalName { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public required PlcTelemetryValueKind ValueKind { get; init; }
    public bool? BoolValue { get; init; }
    public double? NumericValue { get; init; }
    public string? TextValue { get; init; }
    public int Quality { get; init; } = 1;
    public bool ValidatorPassed { get; init; }
    public string? ValidatorReason { get; init; }
    public string? DomainEventType { get; init; }
    public string Source { get; init; } = "PLC";
}

public sealed record PlcTelemetryStatus
{
    public required string Provider { get; init; }
    public long Accepted { get; init; }
    public long Persisted { get; init; }
    public long Replayed { get; init; }
    public long Spooled { get; init; }
    public long Dropped { get; init; }
    public long FailedBatches { get; init; }
    public long QueueDepth { get; init; }
    public long SpoolPending { get; init; }
    public long InFlight { get; init; }
    public long ConservationDelta { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    public string? LastError { get; init; }
}

/// <summary>PLC 事件生产端只依赖该接口，不感知最终数据库。</summary>
public interface IPlcTelemetrySink
{
    ValueTask<bool> EnqueueAsync(
        PlcTelemetryPoint point,
        CancellationToken cancellationToken = default);
}

public interface IPlcTelemetryStore
{
    string ProviderName { get; }

    Task WriteBatchAsync(
        IReadOnlyList<PlcTelemetryPoint> points,
        CancellationToken cancellationToken = default);
}

public interface IPlcTelemetryStatusProvider
{
    PlcTelemetryStatus GetStatus();
}
