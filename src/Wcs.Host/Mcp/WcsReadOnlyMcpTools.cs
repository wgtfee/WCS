using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Server;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

namespace Wcs.Host.Mcp;

[McpServerToolType]
public sealed class WcsReadOnlyMcpTools
{
    [McpServerTool(Name = "wcs_get_device_state", UseStructuredContent = true)]
    [Description("Read the current in-memory WCS state for one device. Read-only: does not send commands, write PLC data, or change WCS state.")]
    public WcsDeviceStateResult GetDeviceState(
        [FromServices] IStateCenter stateCenter,
        [Description("Stable WCS device identifier.")] string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return new WcsDeviceStateResult(false, null);

        var state = stateCenter.GetDeviceState(deviceId.Trim());
        return state is null
            ? new WcsDeviceStateResult(false, null)
            : new WcsDeviceStateResult(true, Map(state));
    }

    [McpServerTool(Name = "wcs_get_active_tasks", UseStructuredContent = true)]
    [Description("Read currently active WCS tasks from the in-memory runtime. Read-only. Internal task parameter dictionaries are intentionally omitted.")]
    public WcsActiveTasksResult GetActiveTasks(
        [FromServices] IStateCenter stateCenter,
        [Description("Maximum number of tasks to return. Required range: 1 to 100.")] int limit)
    {
        var capped = Math.Clamp(limit, 1, 100);
        var tasks = stateCenter.GetAllActiveTasks();
        return new WcsActiveTasksResult(
            tasks.Count,
            tasks.Take(capped).Select(Map).ToArray());
    }

    [McpServerTool(Name = "wcs_get_active_alarms", UseStructuredContent = true)]
    [Description("Read currently active WCS alarms from the in-memory runtime. Read-only and does not acknowledge, recover, or modify alarms.")]
    public WcsActiveAlarmsResult GetActiveAlarms(
        [FromServices] IStateCenter stateCenter,
        [Description("Maximum number of alarms to return. Required range: 1 to 100.")] int limit)
    {
        var capped = Math.Clamp(limit, 1, 100);
        var alarms = stateCenter.GetActiveAlarms();
        return new WcsActiveAlarmsResult(
            alarms.Count,
            alarms.Take(capped).Select(Map).ToArray());
    }

    [McpServerTool(Name = "wcs_get_system_overview", UseStructuredContent = true)]
    [Description("Read a compact WCS runtime overview from StateCenter. Read-only. Returns counts only and does not expose PLC blocks or arbitrary runtime property dictionaries.")]
    public WcsSystemOverview GetSystemOverview([FromServices] IStateCenter stateCenter)
    {
        var devices = stateCenter.GetSnapshot<DeviceState>();
        return new WcsSystemOverview(
            DeviceCount: devices.Count,
            NonOfflineDeviceCount: devices.Count(x => x.Status != DeviceStatusEnum.Offline),
            ErrorDeviceCount: devices.Count(x => x.Status == DeviceStatusEnum.Error),
            ActiveTaskCount: stateCenter.GetAllActiveTasks().Count,
            ActiveAlarmCount: stateCenter.GetActiveAlarms().Count,
            TrackedObjectCount: stateCenter.GetTrackedObjects().Count,
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    private static WcsDeviceStateView Map(DeviceState state) => new(
        state.DeviceId,
        state.Status.ToString(),
        state.CurrentPosition,
        state.LastUpdateTime);

    private static WcsTaskView Map(TaskRuntime task) => new(
        task.TaskId,
        task.Status.ToString(),
        task.Priority,
        task.RouteId,
        task.CreatedTime,
        task.StartTime,
        task.EndTime);

    private static WcsAlarmView Map(AlarmState alarm) => new(
        alarm.AlarmId,
        alarm.AlarmCode,
        alarm.Status.ToString(),
        alarm.Level.ToString(),
        alarm.Message,
        alarm.OccurTime,
        alarm.RootCauseAlarmId,
        alarm.RootCauseDepth);
}

public sealed record WcsDeviceStateResult(bool Found, WcsDeviceStateView? Device);

public sealed record WcsDeviceStateView(
    string DeviceId,
    string Status,
    string? CurrentPosition,
    DateTime LastUpdateTime);

public sealed record WcsActiveTasksResult(int TotalCount, IReadOnlyList<WcsTaskView> Tasks);

public sealed record WcsTaskView(
    string TaskId,
    string Status,
    int Priority,
    string RouteId,
    DateTime CreatedTime,
    DateTime? StartTime,
    DateTime? EndTime);

public sealed record WcsActiveAlarmsResult(int TotalCount, IReadOnlyList<WcsAlarmView> Alarms);

public sealed record WcsAlarmView(
    string AlarmId,
    string AlarmCode,
    string Status,
    string Level,
    string Message,
    DateTime OccurTime,
    string? RootCauseAlarmId,
    int RootCauseDepth);

public sealed record WcsSystemOverview(
    int DeviceCount,
    int NonOfflineDeviceCount,
    int ErrorDeviceCount,
    int ActiveTaskCount,
    int ActiveAlarmCount,
    int TrackedObjectCount,
    DateTimeOffset GeneratedAtUtc);
