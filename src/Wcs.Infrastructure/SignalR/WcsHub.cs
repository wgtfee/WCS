namespace Wcs.Infrastructure.SignalR;

using Microsoft.AspNetCore.SignalR;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// WCS SignalR Hub - 数字孪生和实时 UI
/// </summary>
public class WcsHub : Hub
{
    private static long _connectionCounter;
    private static long _activeConnections;

    public override async Task OnConnectedAsync()
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _connectionCounter);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Interlocked.Decrement(ref _activeConnections);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// 获取活跃连接数
    /// </summary>
    public static long ActiveConnections => Interlocked.Read(ref _activeConnections);

    /// <summary>
    /// 客户端订阅指定设备状态
    /// </summary>
    public async Task SubscribeDevice(string deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"device:{deviceId}");
    }

    /// <summary>
    /// 客户端取消订阅设备
    /// </summary>
    public async Task UnsubscribeDevice(string deviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"device:{deviceId}");
    }

    /// <summary>
    /// 客户端订阅报警
    /// </summary>
    public async Task SubscribeAlarm()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "alarms");
    }
}

/// <summary>
/// SignalR 状态推送服务
/// </summary>
public class SignalRStatePublisher
{
    private readonly IHubContext<WcsHub> _hubContext;

    public SignalRStatePublisher(IHubContext<WcsHub> hubContext)
    {
        _hubContext = hubContext;
    }

    /// <summary>
    /// 推送设备状态变化
    /// </summary>
    public async Task PushDeviceStateAsync(string deviceId, DeviceState state)
    {
        var msg = new DeviceStateChangedMessage(deviceId, state);
        await _hubContext.Clients.Group($"device:{deviceId}").SendAsync("DeviceStateChanged", msg);
        await _hubContext.Clients.All.SendAsync("DeviceStateBroadcast", msg);
    }

    /// <summary>
    /// 推送任务状态变化
    /// </summary>
    public async Task PushTaskStateAsync(string taskId, TaskRuntime runtime)
    {
        var msg = new TaskStateChangedMessage(taskId, runtime);
        await _hubContext.Clients.All.SendAsync("TaskStateChanged", msg);
    }

    /// <summary>
    /// 推送报警
    /// </summary>
    public async Task PushAlarmAsync(string action, object alarm)
    {
        var msg = new AlarmEventMessage(action, alarm);
        await _hubContext.Clients.Group("alarms").SendAsync("AlarmEvent", msg);
        await _hubContext.Clients.All.SendAsync("AlarmBroadcast", msg);
    }

    /// <summary>
    /// 推送物料位置变化
    /// </summary>
    public async Task PushObjectLocationAsync(string objectId, string oldPos, string newPos)
    {
        var msg = new ObjectMovedMessage(objectId, oldPos, newPos);
        await _hubContext.Clients.All.SendAsync("ObjectMoved", msg);
    }
}
