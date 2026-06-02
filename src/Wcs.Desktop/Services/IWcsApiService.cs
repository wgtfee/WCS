namespace Wcs.Desktop.Services;

using Wcs.Core.Recovery;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;

/// <summary>
/// REST API 客户端接口
/// </summary>
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
}
