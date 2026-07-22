namespace Wcs.Core.TransportScheduling;

public enum TransportReadinessSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2,
    Critical = 3
}

public enum TransportReadinessCheckType
{
    RuntimeConfiguration = 0,
    ConfigurationUniqueness = 1,
    VehicleDriverBinding = 2,
    PlcSignalMapCoverage = 3,
    PlcDriverFreshness = 4,
    RuntimeStateStore = 5,
    ConsistencyReport = 6,
    TransportHealth = 7,
    ConfigurationSnapshot = 8,
    LogicalBackup = 9,
    ActiveCommandState = 10
}

public sealed record TransportReadinessCheckItem
{
    public TransportReadinessCheckType CheckType { get; init; }
    public TransportReadinessSeverity Severity { get; init; }
    public bool Passed { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record TransportReadinessReport
{
    public string ReportId { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<TransportReadinessCheckItem> Checks { get; init; } =
        Array.Empty<TransportReadinessCheckItem>();
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
    public int CriticalCount => Checks.Count(x => !x.Passed && x.Severity == TransportReadinessSeverity.Critical);
    public int ErrorCount => Checks.Count(x => !x.Passed && x.Severity == TransportReadinessSeverity.Error);
    public int WarningCount => Checks.Count(x => !x.Passed && x.Severity == TransportReadinessSeverity.Warning);
    public bool IsReady => Success && CriticalCount == 0 && ErrorCount == 0;
}

public sealed record TransportOperationalBaseline
{
    public string BaselineId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CapturedBy { get; init; } = string.Empty;
    public long RuntimeConfigurationVersion { get; init; }
    public long ProductionTuningVersion { get; init; }
    public int VehicleCount { get; init; }
    public int OnlineVehicleCount { get; init; }
    public int ActiveExecutionCount { get; init; }
    public int ActiveReservationCount { get; init; }
    public int ActiveCommandCount { get; init; }
    public int PlcSignalMapCount { get; init; }
    public int PlcDriverOnlineCount { get; init; }
    public int QueueLength { get; init; }
    public TransportHealthSnapshot Health { get; init; } = new();
    public TransportConsistencyReport? Consistency { get; init; }
    public TransportReadinessReport? Readiness { get; init; }
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportLogicalBackupPayload
{
    public int SchemaVersion { get; init; } = 1;
    public TransportRuntimeConfiguration RuntimeConfiguration { get; init; } = new();
    public TransportProductionTuningOptions ProductionTuning { get; init; } = new();
    public IReadOnlyList<TransportStationDefinition> ProductionStations { get; init; } =
        Array.Empty<TransportStationDefinition>();
    public IReadOnlyList<TransportSingleTrackSectionDefinition> SingleTrackSections { get; init; } =
        Array.Empty<TransportSingleTrackSectionDefinition>();
    public IReadOnlyList<TransportPlcSignalMap> PlcSignalMaps { get; init; } =
        Array.Empty<TransportPlcSignalMap>();
    public TransportRuntimeSnapshot RuntimeState { get; init; } = new();
    public IReadOnlyList<TransportJournalRecord> JournalRecords { get; init; } =
        Array.Empty<TransportJournalRecord>();
    public TransportOperationalBaseline Baseline { get; init; } = new();
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportLogicalBackupManifest
{
    public string BackupId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public bool PreflightReady { get; init; }
    public int VehicleCount { get; init; }
    public int ActiveExecutionCount { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportLogicalBackupContent
{
    public TransportLogicalBackupManifest Manifest { get; init; } = new();
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

public enum TransportBackupValidationIssueType
{
    BackupMissing = 0,
    HashMismatch = 1,
    UnsupportedSchema = 2,
    DeserializeFailure = 3,
    DuplicateIdentifier = 4,
    MissingDriverBinding = 5,
    MissingPlcSignalMap = 6,
    RuntimeStateRequiresManualRecovery = 7,
    ActiveCommandRequiresManualRecovery = 8
}

public sealed record TransportBackupValidationIssue
{
    public TransportBackupValidationIssueType IssueType { get; init; }
    public TransportReadinessSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportBackupValidationReport
{
    public string BackupId { get; init; } = string.Empty;
    public bool HashValid { get; init; }
    public bool SchemaValid { get; init; }
    public bool PayloadReadable { get; init; }
    public IReadOnlyList<TransportBackupValidationIssue> Issues { get; init; } =
        Array.Empty<TransportBackupValidationIssue>();
    public DateTime ValidatedAtUtc { get; init; } = DateTime.UtcNow;
    public bool CanPrepareConfigurationRestore =>
        HashValid && SchemaValid && PayloadReadable &&
        !Issues.Any(x => x.Severity is TransportReadinessSeverity.Error or TransportReadinessSeverity.Critical);
}

public sealed record TransportRestorePreparationResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string BackupId { get; init; } = string.Empty;
    public TransportConfigurationSnapshot? ImportedSnapshot { get; init; }
    public IReadOnlyList<string> ManualRecoveryActions { get; init; } = Array.Empty<string>();
}

public enum TransportRecoveryDrillScenario
{
    DriverOffline = 0,
    HeartbeatTimeout = 1,
    StateStoreUnavailable = 2,
    OrphanReservation = 3,
    ConfigurationVersionConflict = 4,
    StaleConsistencyReport = 5,
    ActiveCommandAfterRestart = 6
}

public sealed record TransportRecoveryDrillRequest
{
    public TransportRecoveryDrillScenario Scenario { get; init; }
    public string? TargetVehicleId { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record TransportRecoveryDrillStep
{
    public int Sequence { get; init; }
    public string Action { get; init; } = string.Empty;
    public string ExpectedResult { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportRecoveryDrillReport
{
    public string DrillId { get; init; } = Guid.NewGuid().ToString("N");
    public TransportRecoveryDrillScenario Scenario { get; init; }
    public string? TargetVehicleId { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ExecutedBy { get; init; } = string.Empty;
    public bool IsIsolatedSimulation { get; init; } = true;
    public IReadOnlyList<TransportRecoveryDrillStep> Steps { get; init; } =
        Array.Empty<TransportRecoveryDrillStep>();
    public bool Passed => Steps.Count > 0 && Steps.All(x => x.Passed);
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportResilienceSnapshot
{
    public TransportReadinessReport? LastReadiness { get; init; }
    public TransportOperationalBaseline? LastBaseline { get; init; }
    public TransportLogicalBackupManifest? LastBackup { get; init; }
    public TransportRecoveryDrillReport? LastDrill { get; init; }
    public int BackupCount { get; init; }
    public int DrillCount { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportResilienceOptions
{
    public bool Enabled { get; init; } = true;
    public int PreflightIntervalSeconds { get; init; } = 60;
    public bool AutomaticBackupEnabled { get; init; } = true;
    public int BackupIntervalMinutes { get; init; } = 60;
    public int BackupRetentionCount { get; init; } = 48;
    public int MaximumJournalRecords { get; init; } = 5000;
    public int MaximumBackupAgeMinutes { get; init; } = 180;
    public bool RequireReadyBeforeAutomaticBackup { get; init; }
    public string BackupDirectory { get; init; } = "data/transport-backups";
}
