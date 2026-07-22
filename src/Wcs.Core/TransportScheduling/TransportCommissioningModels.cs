namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using System.Text.Json;
using Wcs.Core.AlarmCenter.Models;

public enum TransportCommunicationOperation
{
    ConnectionCheck = 0,
    BatchRead = 1,
    BatchWrite = 2,
    SingleRead = 3,
    SingleWrite = 4,
    CommandCompensation = 5
}

public sealed record TransportCommunicationTrace
{
    public string TraceId { get; init; } = Guid.NewGuid().ToString("N");
    public string DriverId { get; init; } = string.Empty;
    public string? VehicleId { get; init; }
    public TransportCommunicationOperation Operation { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public bool Success { get; init; }
    public double DurationMs { get; init; }
    public string? Error { get; init; }
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ITransportCommunicationTraceStore
{
    void Append(TransportCommunicationTrace trace);
    IReadOnlyList<TransportCommunicationTrace> GetRecent(
        int maxCount = 500,
        string? driverId = null,
        string? vehicleId = null);
}

public sealed class InMemoryTransportCommunicationTraceStore : ITransportCommunicationTraceStore
{
    private readonly ConcurrentQueue<TransportCommunicationTrace> _traces = new();
    private readonly int _capacity;

    public InMemoryTransportCommunicationTraceStore(int capacity = 2000)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Append(TransportCommunicationTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _traces.Enqueue(trace);
        while (_traces.Count > _capacity && _traces.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<TransportCommunicationTrace> GetRecent(
        int maxCount = 500,
        string? driverId = null,
        string? vehicleId = null)
    {
        maxCount = Math.Clamp(maxCount, 1, _capacity);
        return _traces
            .Where(x => string.IsNullOrWhiteSpace(driverId) ||
                        string.Equals(x.DriverId, driverId, StringComparison.Ordinal))
            .Where(x => string.IsNullOrWhiteSpace(vehicleId) ||
                        string.Equals(x.VehicleId, vehicleId, StringComparison.Ordinal))
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(maxCount)
            .ToArray();
    }
}

public sealed record TransportSignalTemplate
{
    public string TemplateId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public string Protocol { get; init; } = "S7";
    public TransportPlcSignalMap MapPrototype { get; init; } = new();
    public long Version { get; init; }
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportPointTableRow
{
    public string VehicleId { get; init; } = string.Empty;
    public string DriverId { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public TransportDriverMode Mode { get; init; } = TransportDriverMode.PlcTag;
    public bool Enabled { get; init; } = true;
    public int PollIntervalMs { get; init; } = 200;
    public int HeartbeatTimeoutMs { get; init; } = 5000;
    public string HeartbeatTag { get; init; } = string.Empty;
    public string DeviceOnlineTag { get; init; } = string.Empty;
    public string CurrentNodeTag { get; init; } = string.Empty;
    public string OperatingStateTag { get; init; } = string.Empty;
    public string BatteryPercentTag { get; init; } = string.Empty;
    public string FaultCodeTag { get; init; } = string.Empty;
    public string FaultMessageTag { get; init; } = string.Empty;
    public string StateSequenceTag { get; init; } = string.Empty;
    public string ActiveCommandIdTag { get; init; } = string.Empty;
    public string LoadPresentTag { get; init; } = string.Empty;
    public string CommandIdTag { get; init; } = string.Empty;
    public string CommandSequenceTag { get; init; } = string.Empty;
    public string CommandCodeTag { get; init; } = string.Empty;
    public string TargetNodeTag { get; init; } = string.Empty;
    public string CommandRequestTag { get; init; } = string.Empty;
    public string AcknowledgedCommandIdTag { get; init; } = string.Empty;
    public string AcknowledgedSequenceTag { get; init; } = string.Empty;
    public string CommandAcceptedTag { get; init; } = string.Empty;
    public string CommandCompletedTag { get; init; } = string.Empty;
    public string CommandErrorTag { get; init; } = string.Empty;
    public string NodeCodeMapJson { get; init; } = "{}";
    public string TargetNodeCodeMapJson { get; init; } = "{}";
    public string OperatingStateMapJson { get; init; } = "{}";
    public string CommandCodeMapJson { get; init; } = "{}";
    public long ExpectedVersion { get; init; }
}

public enum TransportPointTableIssueLevel
{
    Warning = 0,
    Error = 1
}

public sealed record TransportPointTableIssue
{
    public int RowNumber { get; init; }
    public string Field { get; init; } = string.Empty;
    public TransportPointTableIssueLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportPointTableImportResult
{
    public IReadOnlyList<TransportPointTableRow> Rows { get; init; } = Array.Empty<TransportPointTableRow>();
    public IReadOnlyList<TransportPlcSignalMap> Maps { get; init; } = Array.Empty<TransportPlcSignalMap>();
    public IReadOnlyList<TransportPointTableIssue> Issues { get; init; } = Array.Empty<TransportPointTableIssue>();
    public bool Success => Issues.All(x => x.Level != TransportPointTableIssueLevel.Error);
}

public sealed record TransportSignalProbeResult
{
    public string VehicleId { get; init; } = string.Empty;
    public string DriverId { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public IReadOnlyDictionary<string, object?> Values { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
    public double DurationMs { get; init; }
    public string? Error { get; init; }
    public DateTime ProbedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportSignalValueResult
{
    public string VehicleId { get; init; } = string.Empty;
    public string DriverId { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public object? Value { get; init; }
    public bool Success { get; init; }
    public double DurationMs { get; init; }
    public string? Error { get; init; }
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportFaultDefinition
{
    public string DefinitionId { get; init; } = Guid.NewGuid().ToString("N");
    public TransportVehicleKind Kind { get; init; }
    public int FaultCode { get; init; }
    public string AlarmCode { get; init; } = string.Empty;
    public AlarmLevelEnum Level { get; init; } = AlarmLevelEnum.Error;
    public string Message { get; init; } = string.Empty;
    public string? RecommendedAction { get; init; }
    public bool Enabled { get; init; } = true;
    public long Version { get; init; }
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public enum TransportRecoveryConflictState
{
    Pending = 0,
    Resolved = 1,
    Cancelled = 2
}

public enum TransportRecoveryResolution
{
    AcceptDeviceState = 0,
    KeepPersistedState = 1,
    FailPersistedCommand = 2,
    MarkFieldVerified = 3
}

public sealed record TransportRecoveryConflictCase
{
    public string CaseId { get; init; } = Guid.NewGuid().ToString("N");
    public string VehicleId { get; init; } = string.Empty;
    public TransportDriverReconciliationDecision Decision { get; init; }
    public string PersistedNodeId { get; init; } = string.Empty;
    public string DeviceNodeId { get; init; } = string.Empty;
    public string? PersistedCommandId { get; init; }
    public string? DeviceCommandId { get; init; }
    public string Message { get; init; } = string.Empty;
    public TransportRecoveryConflictState State { get; init; } = TransportRecoveryConflictState.Pending;
    public TransportRecoveryResolution? Resolution { get; init; }
    public string? ResolutionReason { get; init; }
    public string? ResolvedBy { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public enum TransportCommandCompensationDecision
{
    NoAction = 0,
    WaitForReconnect = 1,
    SafeStopRetry = 2,
    RequiresManualConfirmation = 3
}

public sealed record TransportCommandCompensationItem
{
    public string CommandId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public TransportExecutionCommandType CommandType { get; init; }
    public TransportCommandStatus CurrentStatus { get; init; }
    public TransportCommandCompensationDecision Decision { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportCommandCompensationReport
{
    public IReadOnlyList<TransportCommandCompensationItem> Items { get; init; } =
        Array.Empty<TransportCommandCompensationItem>();
    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
    public int SafeRetryCount => Items.Count(x => x.Decision == TransportCommandCompensationDecision.SafeStopRetry);
    public int ManualConfirmationCount => Items.Count(x => x.Decision == TransportCommandCompensationDecision.RequiresManualConfirmation);
}

public enum TransportCommissioningRecordCategory
{
    SignalTemplate = 0,
    FaultDefinition = 1,
    RecoveryConflict = 2
}

public sealed record TransportCommissioningRecord
{
    public TransportCommissioningRecordCategory Category { get; init; }
    public string RecordId { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ITransportCommissioningStore
{
    Task UpsertAsync(TransportCommissioningRecord record, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(TransportCommissioningRecordCategory category, string recordId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportCommissioningRecord>> ListAsync(TransportCommissioningRecordCategory category, CancellationToken cancellationToken = default);
}

public sealed class InMemoryTransportCommissioningStore : ITransportCommissioningStore
{
    private readonly ConcurrentDictionary<string, TransportCommissioningRecord> _records = new(StringComparer.Ordinal);

    public Task UpsertAsync(TransportCommissioningRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records[Key(record.Category, record.RecordId)] = record;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(
        TransportCommissioningRecordCategory category,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_records.TryRemove(Key(category, recordId), out _));
    }

    public Task<IReadOnlyList<TransportCommissioningRecord>> ListAsync(
        TransportCommissioningRecordCategory category,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TransportCommissioningRecord> result = _records.Values
            .Where(x => x.Category == category)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToArray();
        return Task.FromResult(result);
    }

    private static string Key(TransportCommissioningRecordCategory category, string recordId) =>
        $"{(int)category}:{recordId}";
}

internal static class TransportCommissioningJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}
