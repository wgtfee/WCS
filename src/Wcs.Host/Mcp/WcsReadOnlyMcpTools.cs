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
    [Description("Read currently active WCS tasks from the in-memory runtime. Read-only. The result omits internal task parameter dictionaries.")]
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
            ConnectedDeviceCount: devices.Count(x => x.IsConnected),
            FaultedDeviceCount: devices.Count(x => x.IsFaulted),
            ActiveTaskCount: stateCenter.GetAllActiveTasks().Count,
            ActiveAlarmCount: stateCenter.GetActiveAlarms().Count,
            TrackedObjectCount: stateCenter.GetTrackedObjects().Count,
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    private static WcsDeviceStateView Map(DeviceState state) => new(
        state.DeviceId,
        state.DeviceType,
        state.RunState.ToString(),
        state.IsConnected,
        state.IsFaulted,
        state.LastError,
        state.LastHeartbeatTime,
        state.LastUpdateTime);

    private static WcsTaskView Map(TaskRuntime task) => new(
        task.TaskId,
        task.TaskType,
        task.State.ToString(),
        task.Priority,
        task.Source,
        task.Destination,
        task.DeviceId,
        task.CurrentStep,
        task.CreateTime,
        task.StartTime,
        task.Error);

    private static WcsAlarmView Map(AlarmState alarm) => new(
        alarm.AlarmId,
        alarm.DeviceId,
        alarm.AlarmCode,
        alarm.Level.ToString(),
        alarm.Message,
        alarm.Source,
        alarm.RaisedAt);
}

public sealed record WcsDeviceStateResult(bool Found, WcsDeviceStateView? Device);

public sealed record WcsDeviceStateView(
    string DeviceId,
    string DeviceType,
    string RunState,
    bool IsConnected,
    bool IsFaulted,
    string? LastError,
    DateTime? LastHeartbeatTime,
    DateTime LastUpdateTime);

public sealed record WcsActiveTasksResult(int TotalCount, IReadOnlyList<WcsTaskView> Tasks);

public sealed record WcsTaskView(
    string TaskId,
    string TaskType,
    string State,
    int Priority,
    string? Source,
    string? Destination,
    string? DeviceId,
    int CurrentStep,
    DateTime CreateTime,
    DateTime? StartTime,
    string? Error);

public sealed record WcsActiveAlarmsResult(int TotalCount, IReadOnlyList<WcsAlarmView> Alarms);

public sealed record WcsAlarmView(
    string AlarmId,
    string? DeviceId,
    string AlarmCode,
    string Level,
    string Message,
    string? Source,
    DateTime RaisedAt);

public sealed record WcsSystemOverview(
    int DeviceCount,
    int ConnectedDeviceCount,
    int FaultedDeviceCount,
    int ActiveTaskCount,
    int ActiveAlarmCount,
    int TrackedObjectCount,
    DateTimeOffset GeneratedAtUtc);
