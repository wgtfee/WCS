namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public enum TransportDriverMode
{
    Simulation = 0,
    PlcTag = 1
}

public sealed record TransportPlcSignalMap
{
    public string DriverId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public TransportDriverMode Mode { get; init; } = TransportDriverMode.Simulation;
    public bool Enabled { get; init; } = true;
    public long Version { get; init; }
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
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

    public IReadOnlyDictionary<int, string> NodeCodeMap { get; init; } =
        new Dictionary<int, string>();
    public IReadOnlyDictionary<string, int> TargetNodeCodeMap { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<int, TransportVehicleOperatingState> OperatingStateMap { get; init; } =
        new Dictionary<int, TransportVehicleOperatingState>();
    public IReadOnlyDictionary<TransportExecutionCommandType, int> CommandCodeMap { get; init; } =
        new Dictionary<TransportExecutionCommandType, int>();
}

public interface ITransportPlcSignalMapRegistry
{
    void ReplaceAll(IEnumerable<TransportPlcSignalMap> maps);
    void Upsert(TransportPlcSignalMap map);
    bool Remove(string vehicleId);
    bool TryGet(string vehicleId, out TransportPlcSignalMap? map);
    IReadOnlyList<TransportPlcSignalMap> GetAll();
}

public sealed class InMemoryTransportPlcSignalMapRegistry : ITransportPlcSignalMapRegistry
{
    private readonly ConcurrentDictionary<string, TransportPlcSignalMap> _maps = new(StringComparer.Ordinal);

    public void ReplaceAll(IEnumerable<TransportPlcSignalMap> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var normalized = maps.ToArray();
        _maps.Clear();
        foreach (var map in normalized)
            Upsert(map);
    }

    public void Upsert(TransportPlcSignalMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (string.IsNullOrWhiteSpace(map.VehicleId))
            throw new ArgumentException("VehicleId 不能为空", nameof(map));
        _maps[map.VehicleId] = map;
    }

    public bool Remove(string vehicleId) =>
        !string.IsNullOrWhiteSpace(vehicleId) && _maps.TryRemove(vehicleId, out _);

    public bool TryGet(string vehicleId, out TransportPlcSignalMap? map)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            map = null;
            return false;
        }
        return _maps.TryGetValue(vehicleId, out map);
    }

    public IReadOnlyList<TransportPlcSignalMap> GetAll() =>
        _maps.Values.OrderBy(x => x.VehicleId, StringComparer.Ordinal).ToArray();
}

public sealed record TransportDriverDiagnosticSnapshot
{
    public string VehicleId { get; init; } = string.Empty;
    public string DriverId { get; init; } = string.Empty;
    public TransportDriverMode Mode { get; init; }
    public bool AccessorConnected { get; init; }
    public bool DeviceOnline { get; init; }
    public string CurrentNodeId { get; init; } = string.Empty;
    public TransportVehicleOperatingState OperatingState { get; init; }
    public int BatteryPercent { get; init; }
    public int FaultCode { get; init; }
    public string? FaultMessage { get; init; }
    public bool LoadPresent { get; init; }
    public DateTime HeartbeatAtUtc { get; init; }
    public long StateSequence { get; init; }
    public long AcknowledgedSequence { get; init; }
    public string? AcknowledgedCommandId { get; init; }
    public string? PendingCommandId { get; init; }
    public long PendingSequence { get; init; }
    public DateTime? LastReadAtUtc { get; init; }
    public DateTime? LastWriteAtUtc { get; init; }
    public int ConsecutiveReadFailures { get; init; }
    public string? LastError { get; init; }
}

public interface ITransportDriverDiagnosticsService
{
    void Upsert(TransportDriverDiagnosticSnapshot snapshot);
    bool TryGet(string vehicleId, out TransportDriverDiagnosticSnapshot? snapshot);
    IReadOnlyList<TransportDriverDiagnosticSnapshot> GetAll();
}

public sealed class TransportDriverDiagnosticsService : ITransportDriverDiagnosticsService
{
    private readonly ConcurrentDictionary<string, TransportDriverDiagnosticSnapshot> _snapshots = new(StringComparer.Ordinal);

    public void Upsert(TransportDriverDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.VehicleId))
            throw new ArgumentException("VehicleId 不能为空", nameof(snapshot));
        _snapshots[snapshot.VehicleId] = snapshot;
    }

    public bool TryGet(string vehicleId, out TransportDriverDiagnosticSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            snapshot = null;
            return false;
        }
        return _snapshots.TryGetValue(vehicleId, out snapshot);
    }

    public IReadOnlyList<TransportDriverDiagnosticSnapshot> GetAll() =>
        _snapshots.Values.OrderBy(x => x.VehicleId, StringComparer.Ordinal).ToArray();
}

public enum TransportDriverSyncDecision
{
    Updated = 0,
    Offline = 1,
    Faulted = 2,
    SkippedSimulation = 3,
    Failed = 4
}

public sealed record TransportDriverSyncItem
{
    public string VehicleId { get; init; } = string.Empty;
    public TransportDriverSyncDecision Decision { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportDriverSyncReport
{
    public IReadOnlyList<TransportDriverSyncItem> Items { get; init; } = Array.Empty<TransportDriverSyncItem>();
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
    public int UpdatedCount => Items.Count(x => x.Decision == TransportDriverSyncDecision.Updated);
    public int OfflineCount => Items.Count(x => x.Decision == TransportDriverSyncDecision.Offline);
    public int FaultedCount => Items.Count(x => x.Decision == TransportDriverSyncDecision.Faulted);
}

public enum TransportDriverReconciliationDecision
{
    InSync = 0,
    VehicleNotPersisted = 1,
    DeviceOffline = 2,
    PositionMismatch = 3,
    ActiveCommandMismatch = 4,
    RequiresManualConfirmation = 5,
    Failed = 6
}

public sealed record TransportDriverReconciliationItem
{
    public string VehicleId { get; init; } = string.Empty;
    public TransportDriverReconciliationDecision Decision { get; init; }
    public string PersistedNodeId { get; init; } = string.Empty;
    public string DeviceNodeId { get; init; } = string.Empty;
    public string? PersistedCommandId { get; init; }
    public string? DeviceCommandId { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportDriverReconciliationReport
{
    public IReadOnlyList<TransportDriverReconciliationItem> Items { get; init; } = Array.Empty<TransportDriverReconciliationItem>();
    public DateTime ReconciledAtUtc { get; init; } = DateTime.UtcNow;
    public int InSyncCount => Items.Count(x => x.Decision == TransportDriverReconciliationDecision.InSync);
    public int ManualConfirmationCount => Items.Count(x => x.Decision is
        TransportDriverReconciliationDecision.PositionMismatch or
        TransportDriverReconciliationDecision.ActiveCommandMismatch or
        TransportDriverReconciliationDecision.RequiresManualConfirmation);
}
