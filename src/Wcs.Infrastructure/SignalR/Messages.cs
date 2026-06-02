namespace Wcs.Infrastructure.SignalR;

using Wcs.Core.StateCenter.Models;

/// <summary>SignalR 设备状态变化消息</summary>
public record DeviceStateChangedMessage(string DeviceId, DeviceState State);

/// <summary>SignalR 任务状态变化消息</summary>
public record TaskStateChangedMessage(string TaskId, TaskRuntime Runtime);

/// <summary>SignalR 报警事件消息</summary>
public record AlarmEventMessage(string Action, object Alarm);

/// <summary>SignalR 物料位置变化消息</summary>
public record ObjectMovedMessage(string ObjectId, string OldPos, string NewPos);
