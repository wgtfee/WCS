namespace Wcs.Core.TransportScheduling;

public static class TransportTelemetryNames
{
    public const string ActivitySourceName = "Wcs.Transport";
    public const string MeterName = "Wcs.Transport";
    public const string ServiceName = "wcs-runtime-engine";
}

public enum TransportTraceOperationKind
{
    Dispatch = 0,
    PlcCommand = 1,
    ConsistencyInspection = 2,
    HealthEvaluation = 3,
    ConfigurationSnapshot = 4,
    ConfigurationRollback = 5,
    ResiliencePreflight = 6,
    LogicalBackup = 7,
    RecoveryDrill = 8,
    RestorePreparation = 9,
    Simulation = 10,
    StrategyComparison = 11,
    CapacityBenchmark = 12,
    FinalAcceptance = 13
}

public sealed record TransportTraceRecord
{
    public string TraceId { get; init; } = string.Empty;
    public string SpanId { get; init; } = string.Empty;
    public string? ParentSpanId { get; init; }
    public TransportTraceOperationKind Kind { get; init; }
    public string OperationName { get; init; } = string.Empty;
    public string? RequestId { get; init; }
    public string? VehicleId { get; init; }
    public bool Success { get; init; }
    public double DurationMilliseconds { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; init; }
}

public sealed record TransportOperationMetric
{
    public TransportTraceOperationKind Kind { get; init; }
    public long TotalCount { get; init; }
    public long FailureCount { get; init; }
    public double AverageDurationMilliseconds { get; init; }
    public double MaximumDurationMilliseconds { get; init; }
}

public sealed record TransportTelemetryMetricsSnapshot
{
    public IReadOnlyList<TransportOperationMetric> Operations { get; init; } =
        Array.Empty<TransportOperationMetric>();
    public long ConsistencyIssueCount { get; init; }
    public double LastQueueWaitMilliseconds { get; init; }
    public double LastPlcResponseMilliseconds { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public enum TransportConsistencySeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
    Critical = 3
}

public enum TransportConsistencyIssueType
{
    PersistedVehicleMissing = 0,
    RuntimeVehicleMissing = 1,
    VehiclePositionMismatch = 2,
    VehicleOnlineStateMismatch = 3,
    PersistedExecutionMissing = 4,
    RuntimeExecutionMissing = 5,
    ExecutionStateMismatch = 6,
    PersistedReservationMissing = 7,
    RuntimeReservationMissing = 8,
    PlcDeviceOffline = 9,
    PlcPositionMismatch = 10,
    PlcCommandMismatch = 11,
    InspectionFailure = 12
}

public sealed record TransportConsistencyIssue
{
    public string IssueId { get; init; } = Guid.NewGuid().ToString("N");
    public TransportConsistencyIssueType IssueType { get; init; }
    public TransportConsistencySeverity Severity { get; init; }
    public string EntityType { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public string RuntimeValue { get; init; } = string.Empty;
    public string PersistedValue { get; init; } = string.Empty;
    public string? PlcValue { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime DetectedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportConsistencyReport
{
    public string ReportId { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<TransportConsistencyIssue> Issues { get; init; } =
        Array.Empty<TransportConsistencyIssue>();
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
    public int CriticalCount => Issues.Count(x => x.Severity == TransportConsistencySeverity.Critical);
    public int ErrorCount => Issues.Count(x => x.Severity == TransportConsistencySeverity.Error);
    public int WarningCount => Issues.Count(x => x.Severity == TransportConsistencySeverity.Warning);
    public bool IsConsistent => Success && Issues.Count == 0;
}

public enum TransportHealthState
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2
}

public sealed record TransportHealthComponent
{
    public string Component { get; init; } = string.Empty;
    public TransportHealthState State { get; init; }
    public int Score { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportHealthSnapshot
{
    public TransportHealthState State { get; init; }
    public int Score { get; init; }
    public IReadOnlyList<TransportHealthComponent> Components { get; init; } =
        Array.Empty<TransportHealthComponent>();
    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportObservabilitySnapshot
{
    public TransportHealthSnapshot Health { get; init; } = new();
    public TransportTelemetryMetricsSnapshot Metrics { get; init; } = new();
    public TransportConsistencyReport? LastConsistencyReport { get; init; }
    public int OnlineVehicleCount { get; init; }
    public int OfflineVehicleCount { get; init; }
    public int ActiveExecutionCount { get; init; }
    public int QueueLength { get; init; }
    public int ActiveReservationCount { get; init; }
    public int ActiveAlarmCount { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportObservabilityOptions
{
    public bool Enabled { get; init; } = true;
    public int ConsistencyInspectionIntervalSeconds { get; init; } = 30;
    public int HealthEvaluationIntervalSeconds { get; init; } = 10;
    public int TraceRetentionCount { get; init; } = 5000;
    public int DegradedScoreThreshold { get; init; } = 80;
    public int UnhealthyScoreThreshold { get; init; } = 50;
    public bool RaiseConsistencyAlarms { get; init; } = true;
    public bool EnablePrometheusEndpoint { get; init; } = true;
    public bool EnableOtlpExporter { get; init; }
    public string? OtlpEndpoint { get; init; }
}

public sealed record TransportConfigurationSnapshot
{
    public string SnapshotId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public TransportRuntimeConfiguration RuntimeConfiguration { get; init; } = new();
    public TransportProductionTuningOptions ProductionTuning { get; init; } = new();
    public IReadOnlyList<TransportStationDefinition> ProductionStations { get; init; } =
        Array.Empty<TransportStationDefinition>();
    public IReadOnlyList<TransportSingleTrackSectionDefinition> SingleTrackSections { get; init; } =
        Array.Empty<TransportSingleTrackSectionDefinition>();
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportConfigurationRollbackResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? SafetySnapshotId { get; init; }
    public TransportConfigurationSnapshot? AppliedSnapshot { get; init; }
}
