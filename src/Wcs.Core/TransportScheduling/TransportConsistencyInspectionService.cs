namespace Wcs.Core.TransportScheduling;

using System.Text.Json;
using Wcs.Core.AlarmCenter;
using Wcs.Core.StateCenter.Models;

public interface ITransportConsistencyInspectionService
{
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task<TransportConsistencyReport> InspectAsync(CancellationToken cancellationToken = default);
    TransportConsistencyReport? GetLastReport();
    IReadOnlyList<TransportConsistencyReport> GetRecentReports(int maxCount = 100);
}

public sealed class TransportConsistencyInspectionService : ITransportConsistencyInspectionService
{
    private const string AlarmCode = "TRANSPORT_CONSISTENCY";
    private readonly ITransportStateStore _stateStore;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportExecutionEngine _executions;
    private readonly IRouteReservationManager _reservations;
    private readonly ITransportDriverDiagnosticsService _diagnostics;
    private readonly ITransportTelemetryService _telemetry;
    private readonly ITransportJournalStore _journal;
    private readonly IAlarmCenter _alarms;
    private readonly object _sync = new();
    private readonly List<TransportConsistencyReport> _reports = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TransportConsistencyInspectionService(
        ITransportStateStore stateStore,
        ITransportVehicleRegistry vehicles,
        ITransportExecutionEngine executions,
        IRouteReservationManager reservations,
        ITransportDriverDiagnosticsService diagnostics,
        ITransportTelemetryService telemetry,
        ITransportJournalStore journal,
        IAlarmCenter alarms)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ConsistencyReport,
            100,
            cancellationToken).ConfigureAwait(false);
        var restored = records
            .Select(x => JsonSerializer.Deserialize<TransportConsistencyReport>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportConsistencyReport>()
            .OrderBy(x => x.CompletedAtUtc)
            .ToArray();
        lock (_sync)
        {
            _reports.Clear();
            _reports.AddRange(restored);
            TrimUnsafe();
        }
    }

    public async Task<TransportConsistencyReport> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = DateTime.UtcNow;
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.ConsistencyInspection,
            "transport.consistency.inspect");
        try
        {
            var persisted = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var runtimeVehicles = _vehicles.GetAll();
            var runtimeExecutions = _executions.GetAll();
            var runtimeReservations = _reservations.GetActiveReservations();
            var diagnostics = _diagnostics.GetAll();
            var issues = new List<TransportConsistencyIssue>();

            CompareVehicles(runtimeVehicles, persisted.Vehicles, issues);
            CompareExecutions(runtimeExecutions, persisted.Executions, issues);
            CompareReservations(runtimeReservations, persisted.Reservations, issues);
            ComparePlc(runtimeVehicles, persisted.Commands, diagnostics, issues);

            var report = new TransportConsistencyReport
            {
                Issues = issues,
                StartedAtUtc = started,
                CompletedAtUtc = DateTime.UtcNow
            };
            await SaveAsync(report, cancellationToken).ConfigureAwait(false);
            await UpdateAlarmAsync(report, cancellationToken).ConfigureAwait(false);

            if (issues.Count > 0)
            {
                _telemetry.RecordConsistencyIssues(
                    issues.Count,
                    issues.Max(x => x.Severity));
            }
            operation.Complete(
                report.IsConsistent,
                report.IsConsistent ? "三方状态一致" : $"发现 {issues.Count} 项状态差异",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["issues.total"] = issues.Count.ToString(),
                    ["issues.critical"] = report.CriticalCount.ToString(),
                    ["issues.error"] = report.ErrorCount.ToString(),
                    ["issues.warning"] = report.WarningCount.ToString()
                });
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var report = new TransportConsistencyReport
            {
                Success = false,
                Error = ex.Message,
                StartedAtUtc = started,
                CompletedAtUtc = DateTime.UtcNow,
                Issues = new[]
                {
                    Issue(
                        TransportConsistencyIssueType.InspectionFailure,
                        TransportConsistencySeverity.Critical,
                        "Inspection",
                        "transport",
                        string.Empty,
                        string.Empty,
                        null,
                        $"一致性巡检失败：{ex.Message}")
                }
            };
            await SaveAsync(report, cancellationToken).ConfigureAwait(false);
            await UpdateAlarmAsync(report, cancellationToken).ConfigureAwait(false);
            _telemetry.RecordConsistencyIssues(1, TransportConsistencySeverity.Critical);
            operation.Complete(false, ex.Message);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportConsistencyReport? GetLastReport()
    {
        lock (_sync)
            return _reports.OrderByDescending(x => x.CompletedAtUtc).FirstOrDefault();
    }

    public IReadOnlyList<TransportConsistencyReport> GetRecentReports(int maxCount = 100)
    {
        lock (_sync)
        {
            return _reports
                .OrderByDescending(x => x.CompletedAtUtc)
                .Take(Math.Clamp(maxCount, 1, 100))
                .ToArray();
        }
    }

    private static void CompareVehicles(
        IReadOnlyList<TransportVehicleSnapshot> runtime,
        IReadOnlyList<TransportVehicleSnapshot> persisted,
        ICollection<TransportConsistencyIssue> issues)
    {
        var runtimeById = runtime.ToDictionary(x => x.VehicleId, StringComparer.Ordinal);
        var persistedById = persisted.ToDictionary(x => x.VehicleId, StringComparer.Ordinal);
        foreach (var vehicle in runtime)
        {
            if (!persistedById.TryGetValue(vehicle.VehicleId, out var saved))
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.PersistedVehicleMissing,
                    TransportConsistencySeverity.Error,
                    "Vehicle",
                    vehicle.VehicleId,
                    Describe(vehicle),
                    string.Empty,
                    null,
                    "内存中存在车辆，但数据库没有车辆快照"));
                continue;
            }
            if (!string.Equals(vehicle.CurrentNodeId, saved.CurrentNodeId, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.VehiclePositionMismatch,
                    TransportConsistencySeverity.Error,
                    "Vehicle",
                    vehicle.VehicleId,
                    vehicle.CurrentNodeId,
                    saved.CurrentNodeId,
                    null,
                    "内存与数据库车辆位置不一致"));
            }
            if (vehicle.IsOnline != saved.IsOnline)
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.VehicleOnlineStateMismatch,
                    TransportConsistencySeverity.Warning,
                    "Vehicle",
                    vehicle.VehicleId,
                    vehicle.IsOnline.ToString(),
                    saved.IsOnline.ToString(),
                    null,
                    "内存与数据库车辆在线状态不一致"));
            }
        }
        foreach (var saved in persisted.Where(x => !runtimeById.ContainsKey(x.VehicleId)))
        {
            issues.Add(Issue(
                TransportConsistencyIssueType.RuntimeVehicleMissing,
                TransportConsistencySeverity.Error,
                "Vehicle",
                saved.VehicleId,
                string.Empty,
                Describe(saved),
                null,
                "数据库存在车辆快照，但运行时注册表没有该车辆"));
        }
    }

    private static void CompareExecutions(
        IReadOnlyList<TransportExecutionSnapshot> runtime,
        IReadOnlyList<TransportExecutionSnapshot> persisted,
        ICollection<TransportConsistencyIssue> issues)
    {
        var runtimeActive = runtime.Where(x => !x.IsTerminal).ToDictionary(x => x.RequestId, StringComparer.Ordinal);
        var persistedActive = persisted.Where(x => !x.IsTerminal).ToDictionary(x => x.RequestId, StringComparer.Ordinal);
        foreach (var execution in runtimeActive.Values)
        {
            if (!persistedActive.TryGetValue(execution.RequestId, out var saved))
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.PersistedExecutionMissing,
                    TransportConsistencySeverity.Critical,
                    "Execution",
                    execution.RequestId,
                    execution.State.ToString(),
                    string.Empty,
                    null,
                    "运行时存在活动任务，但数据库没有活动执行快照"));
                continue;
            }
            if (execution.State != saved.State ||
                !string.Equals(execution.VehicleId, saved.VehicleId, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.ExecutionStateMismatch,
                    TransportConsistencySeverity.Critical,
                    "Execution",
                    execution.RequestId,
                    $"{execution.VehicleId}:{execution.State}",
                    $"{saved.VehicleId}:{saved.State}",
                    null,
                    "活动任务的车辆或状态在内存与数据库之间不一致"));
            }
        }
        foreach (var saved in persistedActive.Values.Where(x => !runtimeActive.ContainsKey(x.RequestId)))
        {
            issues.Add(Issue(
                TransportConsistencyIssueType.RuntimeExecutionMissing,
                TransportConsistencySeverity.Critical,
                "Execution",
                saved.RequestId,
                string.Empty,
                $"{saved.VehicleId}:{saved.State}",
                null,
                "数据库存在活动任务，但运行时执行引擎没有该任务"));
        }
    }

    private static void CompareReservations(
        IReadOnlyList<RouteReservation> runtime,
        IReadOnlyList<RouteReservation> persisted,
        ICollection<TransportConsistencyIssue> issues)
    {
        var runtimeIds = runtime.Select(x => x.ReservationId).ToHashSet(StringComparer.Ordinal);
        var persistedIds = persisted.Select(x => x.ReservationId).ToHashSet(StringComparer.Ordinal);
        foreach (var reservation in runtime.Where(x => !persistedIds.Contains(x.ReservationId)))
        {
            issues.Add(Issue(
                TransportConsistencyIssueType.PersistedReservationMissing,
                TransportConsistencySeverity.Critical,
                "Reservation",
                reservation.ReservationId,
                string.Join(',', reservation.EdgeIds),
                string.Empty,
                null,
                "运行时存在路权预留，但数据库没有对应记录"));
        }
        foreach (var reservation in persisted.Where(x => !runtimeIds.Contains(x.ReservationId)))
        {
            issues.Add(Issue(
                TransportConsistencyIssueType.RuntimeReservationMissing,
                TransportConsistencySeverity.Error,
                "Reservation",
                reservation.ReservationId,
                string.Empty,
                string.Join(',', reservation.EdgeIds),
                null,
                "数据库存在路权预留，但运行时没有对应记录"));
        }
    }

    private static void ComparePlc(
        IReadOnlyList<TransportVehicleSnapshot> runtimeVehicles,
        IReadOnlyList<TransportCommandRecord> persistedCommands,
        IReadOnlyList<TransportDriverDiagnosticSnapshot> diagnostics,
        ICollection<TransportConsistencyIssue> issues)
    {
        var runtimeById = runtimeVehicles.ToDictionary(x => x.VehicleId, StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(x => x.Mode == TransportDriverMode.PlcTag))
        {
            if (!diagnostic.AccessorConnected || !diagnostic.DeviceOnline)
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.PlcDeviceOffline,
                    TransportConsistencySeverity.Error,
                    "Vehicle",
                    diagnostic.VehicleId,
                    runtimeById.GetValueOrDefault(diagnostic.VehicleId)?.IsOnline.ToString() ?? string.Empty,
                    string.Empty,
                    diagnostic.DeviceOnline.ToString(),
                    "PLC 访问器或设备处于离线状态"));
            }
            if (runtimeById.TryGetValue(diagnostic.VehicleId, out var vehicle) &&
                diagnostic.DeviceOnline &&
                !string.IsNullOrWhiteSpace(vehicle.CurrentNodeId) &&
                !string.IsNullOrWhiteSpace(diagnostic.CurrentNodeId) &&
                !string.Equals(vehicle.CurrentNodeId, diagnostic.CurrentNodeId, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.PlcPositionMismatch,
                    TransportConsistencySeverity.Critical,
                    "Vehicle",
                    diagnostic.VehicleId,
                    vehicle.CurrentNodeId,
                    string.Empty,
                    diagnostic.CurrentNodeId,
                    "内存车辆位置与 PLC 可信节点不一致"));
            }

            var activeCommand = persistedCommands
                .Where(x => string.Equals(x.VehicleId, diagnostic.VehicleId, StringComparison.Ordinal))
                .Where(x => x.Status is TransportCommandStatus.Pending or TransportCommandStatus.Sent or TransportCommandStatus.Acknowledged)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();
            var plcCommandId = diagnostic.PendingCommandId ?? diagnostic.AcknowledgedCommandId;
            if (activeCommand is not null &&
                !string.IsNullOrWhiteSpace(plcCommandId) &&
                !string.Equals(activeCommand.CommandId, plcCommandId, StringComparison.Ordinal))
            {
                issues.Add(Issue(
                    TransportConsistencyIssueType.PlcCommandMismatch,
                    TransportConsistencySeverity.Critical,
                    "Command",
                    activeCommand.CommandId,
                    activeCommand.Status.ToString(),
                    activeCommand.CommandId,
                    plcCommandId,
                    "数据库活动命令与 PLC 当前命令不一致"));
            }
        }
    }

    private async Task SaveAsync(
        TransportConsistencyReport report,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _reports.Add(report);
            TrimUnsafe();
        }
        await _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ConsistencyReport,
            RecordId = report.ReportId,
            PayloadJson = JsonSerializer.Serialize(report),
            OccurredAtUtc = report.CompletedAtUtc
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAlarmAsync(
        TransportConsistencyReport report,
        CancellationToken cancellationToken)
    {
        if (report.IsConsistent)
        {
            await _alarms.RecoverAlarmAsync(AlarmCode, cancellationToken).ConfigureAwait(false);
            return;
        }

        var level = report.CriticalCount > 0
            ? AlarmLevelEnum.Critical
            : report.ErrorCount > 0
                ? AlarmLevelEnum.Error
                : AlarmLevelEnum.Warning;
        await _alarms.RaiseAlarmAsync(
            AlarmCode,
            level,
            report.Success
                ? $"EMS/RGV 三方一致性巡检发现 {report.Issues.Count} 项差异"
                : $"EMS/RGV 三方一致性巡检执行失败：{report.Error}",
            source: "TransportConsistencyInspection",
            alarmGroup: "TransportObservability",
            ct: cancellationToken).ConfigureAwait(false);
    }

    private void TrimUnsafe()
    {
        const int capacity = 100;
        var excess = _reports.Count - capacity;
        if (excess > 0)
            _reports.RemoveRange(0, excess);
    }

    private static string Describe(TransportVehicleSnapshot vehicle) =>
        $"{vehicle.State}@{vehicle.CurrentNodeId},online={vehicle.IsOnline},v={vehicle.Version}";

    private static TransportConsistencyIssue Issue(
        TransportConsistencyIssueType type,
        TransportConsistencySeverity severity,
        string entityType,
        string entityId,
        string runtimeValue,
        string persistedValue,
        string? plcValue,
        string message) => new()
        {
            IssueType = type,
            Severity = severity,
            EntityType = entityType,
            EntityId = entityId,
            RuntimeValue = runtimeValue,
            PersistedValue = persistedValue,
            PlcValue = plcValue,
            Message = message
        };
}
