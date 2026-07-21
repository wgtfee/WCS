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

    Task<List<AlarmState>> GetAlarmsFromDbAsync(CancellationToken ct = default);
    Task<List<AlarmState>> GetAlarmHistoryAsync(DateTime? from = null, DateTime? to = null,
        string? level = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
    Task<List<TaskContext>> GetTasksFromDbAsync(CancellationToken ct = default);
    Task<List<TaskContext>> GetTaskHistoryAsync(DateTime? from = null, DateTime? to = null,
        string? status = null, int page = 1, int pageSize = 50, CancellationToken ct = default);
    Task<List<DeviceState>> GetDevicesFromDbAsync(CancellationToken ct = default);
}
