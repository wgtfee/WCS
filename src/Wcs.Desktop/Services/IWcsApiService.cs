using Wcs.Core.Recovery;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Models;
using Wcs.Entity;

namespace Wcs.Desktop.Services;

public interface IWcsApiService
{
    Task<SystemOverview?> GetOverviewAsync(CancellationToken ct = default);
    Task<List<DeviceState>> GetDevicesAsync(CancellationToken ct = default);
    Task<DeviceState?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
    Task<List<TaskContext>> GetActiveTasksAsync(CancellationToken ct = default);
    Task<TaskContext?> CreateTaskAsync(string deviceId, string routeId, int priority = 2,
        Dictionary<string, object>? parameters = null, CancellationToken ct = default);
    Task<bool> CancelTaskAsync(string taskId, CancellationToken ct = default);
    Task<List<AlarmState>> GetAlarmsAsync(CancellationToken ct = default);
    Task AckAlarmAsync(string alarmId, CancellationToken ct = default);
    Task RecoverAlarmAsync(string alarmCode, CancellationToken ct = default);
    Task<List<ObjectState>> GetObjectsAsync(CancellationToken ct = default);
    Task<RecoveryResult?> RecoverAsync(CancellationToken ct = default);
    Task<List<MenuItemDto>> GetMenusAsync(CancellationToken ct = default);

    Task<List<TransportVehicleSnapshot>> GetTransportVehiclesAsync(CancellationToken ct = default);
    Task<List<TransportExecutionSnapshot>> GetTransportExecutionsAsync(CancellationToken ct = default);
    Task<List<RouteReservation>> GetTransportReservationsAsync(CancellationToken ct = default);
    Task<TransportTrafficSnapshot?> GetTransportTrafficAsync(CancellationToken ct = default);
    Task<List<TransportDeadlockCycle>> GetTransportDeadlocksAsync(CancellationToken ct = default);

    Task<List<TransportChargingStationSnapshot>> GetTransportChargingStationsAsync(CancellationToken ct = default);
    Task<List<TransportChargingPlan>> GetTransportChargingPlansAsync(CancellationToken ct = default);
    Task<List<TransportTaskReassignmentRecord>> GetTransportReassignmentsAsync(CancellationToken ct = default);
    Task<TransportPerformanceSnapshot?> GetTransportPerformanceAsync(CancellationToken ct = default);
    Task<List<TransportChargingEvaluation>> EvaluateTransportChargingAsync(CancellationToken ct = default);

    Task<TransportRuntimeConfiguration?> GetTransportConfigurationAsync(CancellationToken ct = default);
    Task<List<TransportGovernedOperation>> GetTransportGovernedOperationsAsync(CancellationToken ct = default);
    Task<List<TransportAuditRecord>> GetTransportAuditsAsync(CancellationToken ct = default);
    Task<List<TransportJournalRecord>> GetTransportJournalAsync(
        TransportJournalCategory? category = null,
        CancellationToken ct = default);

    Task<List<TransportPlcSignalMap>> GetTransportPlcSignalMapsAsync(CancellationToken ct = default);
    Task<List<TransportDriverDiagnosticSnapshot>> GetTransportDriverDiagnosticsAsync(CancellationToken ct = default);
    Task<TransportDriverSyncReport?> PollTransportDriversAsync(CancellationToken ct = default);
    Task<TransportDriverReconciliationReport?> ReconcileTransportDriversAsync(CancellationToken ct = default);

    Task<List<TransportSignalTemplate>> GetTransportSignalTemplatesAsync(CancellationToken ct = default);
    Task<List<TransportFaultDefinition>> GetTransportFaultDefinitionsAsync(CancellationToken ct = default);
    Task<List<TransportRecoveryConflictCase>> GetTransportRecoveryConflictsAsync(CancellationToken ct = default);
    Task<List<TransportRecoveryConflictCase>> RefreshTransportRecoveryConflictsAsync(CancellationToken ct = default);
    Task<TransportCommandCompensationReport?> GetTransportCommandCompensationAsync(CancellationToken ct = default);
    Task<List<TransportCommunicationTrace>> GetTransportCommunicationTracesAsync(int maxCount = 500, CancellationToken ct = default);
    Task<TransportSignalProbeResult?> ProbeTransportVehicleAsync(string vehicleId, CancellationToken ct = default);

    Task<TransportProductionTuningOptions?> GetTransportProductionTuningAsync(CancellationToken ct = default);
    Task<List<TransportStationRuntimeSnapshot>> GetTransportProductionStationsAsync(CancellationToken ct = default);
    Task<List<TransportSingleTrackSectionSnapshot>> GetTransportSingleTrackAsync(CancellationToken ct = default);
    Task<List<TransportProductionQueueItem>> GetTransportProductionQueueAsync(CancellationToken ct = default);
    Task<TransportProductionDispatchCycleResult?> RunTransportProductionDispatchCycleAsync(CancellationToken ct = default);
    Task<TransportProductionDryRunReport?> GetTransportProductionDryRunAsync(CancellationToken ct = default);
    Task<List<TransportDispatchDecisionFrame>> GetTransportDispatchDecisionsAsync(int maxCount = 500, CancellationToken ct = default);
    Task<TransportProductionTrendSummary?> GetTransportProductionTrendsAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default);
    Task<TransportFaultTakeoverReport?> EvaluateTransportFaultTakeoverAsync(CancellationToken ct = default);

    Task<TransportObservabilitySnapshot?> GetTransportObservabilityAsync(CancellationToken ct = default);
    Task<TransportHealthSnapshot?> EvaluateTransportHealthAsync(CancellationToken ct = default);
    Task<TransportConsistencyReport?> InspectTransportConsistencyAsync(CancellationToken ct = default);
    Task<List<TransportTraceRecord>> GetTransportTracesAsync(int maxCount = 500, CancellationToken ct = default);
    Task<List<TransportConfigurationSnapshot>> GetTransportConfigurationSnapshotsAsync(int maxCount = 100, CancellationToken ct = default);

    Task<TransportResilienceSnapshot?> GetTransportResilienceAsync(CancellationToken ct = default);
    Task<TransportReadinessReport?> RunTransportReadinessAsync(CancellationToken ct = default);
    Task<List<TransportOperationalBaseline>> GetTransportBaselinesAsync(int maxCount = 100, CancellationToken ct = default);
    Task<List<TransportLogicalBackupManifest>> GetTransportBackupsAsync(int maxCount = 100, CancellationToken ct = default);
    Task<TransportBackupValidationReport?> ValidateTransportBackupAsync(string backupId, CancellationToken ct = default);
    Task<List<TransportRecoveryDrillReport>> GetTransportRecoveryDrillsAsync(int maxCount = 100, CancellationToken ct = default);

    Task<List<AlarmState>> GetAlarmsFromDbAsync(CancellationToken ct = default);
    Task<List<AlarmState>> GetAlarmHistoryAsync(DateTime? from = null, DateTime? to = null,
        string? level = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
    Task<List<TaskContext>> GetTasksFromDbAsync(CancellationToken ct = default);
    Task<List<TaskContext>> GetTaskHistoryAsync(DateTime? from = null, DateTime? to = null,
        string? status = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
    Task<List<DeviceState>> GetDevicesFromDbAsync(CancellationToken ct = default);
}
