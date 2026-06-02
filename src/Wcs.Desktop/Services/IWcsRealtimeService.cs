using Wcs.Core.StateCenter.Models;

namespace Wcs.Desktop.Services;

/// <summary>
/// SignalR 实时推送客户端接口
/// </summary>
public interface IWcsRealtimeService
{
    bool IsConnected { get; }
    event Action<bool>? ConnectionStateChanged;

    Task ConnectAsync(string serverUrl, CancellationToken ct = default);
    Task DisconnectAsync();
    Task SubscribeDeviceAsync(string deviceId);
    Task UnsubscribeDeviceAsync(string deviceId);
    Task SubscribeAlarmAsync();

    event Action<DeviceStateChangedMessage>? DeviceStateChanged;
    event Action<DeviceStateChangedMessage>? DeviceStateBroadcast;
    event Action<TaskStateChangedMessage>? TaskStateChanged;
    event Action<AlarmEventMessage>? AlarmEvent;
    event Action<AlarmEventMessage>? AlarmBroadcast;
    event Action<ObjectMovedMessage>? ObjectMoved;
}

public record DeviceStateChangedMessage(string DeviceId, DeviceState State);
public record TaskStateChangedMessage(string TaskId, TaskRuntime Runtime);
public record AlarmEventMessage(string Action, object Alarm);
public record ObjectMovedMessage(string ObjectId, string OldPos, string NewPos);
