using Avalonia.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using Wcs.Desktop.Interface;

namespace Wcs.Desktop.Services;

/// <summary>
/// SignalR 实时推送客户端 - HubConnection 包装
/// </summary>
public class WcsRealtimeService : IWcsRealtimeService, IAsyncDisposable
{
    private readonly IAuthState _authState;
    private HubConnection? _connection;
    private bool _isConnected;

    public WcsRealtimeService(IAuthState authState)
    {
        _authState = authState;
    }

    public bool IsConnected => _isConnected;
    public event Action<bool>? ConnectionStateChanged;
    public event Action<DeviceStateChangedMessage>? DeviceStateChanged;
    public event Action<DeviceStateChangedMessage>? DeviceStateBroadcast;
    public event Action<TaskStateChangedMessage>? TaskStateChanged;
    public event Action<AlarmEventMessage>? AlarmEvent;
    public event Action<AlarmEventMessage>? AlarmBroadcast;
    public event Action<ObjectMovedMessage>? ObjectMoved;

    public async Task ConnectAsync(string serverUrl, CancellationToken ct = default)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl.TrimEnd('/')}/wcs", options =>
            {
                // SignalR transports use AccessTokenProvider for HTTP negotiation and
                // automatically map the token to the access_token query parameter when
                // WebSockets/SSE require it.
                options.AccessTokenProvider = () => Task.FromResult<string?>(
                    string.IsNullOrWhiteSpace(_authState.Token) ? null : _authState.Token);
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.Reconnecting += _ =>
        {
            SetConnected(false);
            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            SetConnected(true);
            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            SetConnected(false);
            return Task.CompletedTask;
        };

        RegisterHandlers();
        await _connection.StartAsync(ct);
        SetConnected(true);
    }

    public async Task DisconnectAsync()
    {
        if (_connection is not null)
        {
            await _connection.StopAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
        SetConnected(false);
    }

    public async Task SubscribeDeviceAsync(string deviceId)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("SubscribeDevice", deviceId);
    }

    public async Task UnsubscribeDeviceAsync(string deviceId)
    {
        if (_connection is not null)
            await _connection.InvokeAsync("UnsubscribeDevice", deviceId);
    }

    public async Task SubscribeAlarmAsync()
    {
        if (_connection is not null)
            await _connection.InvokeAsync("SubscribeAlarm");
    }

    private void RegisterHandlers()
    {
        if (_connection is null) return;

        _connection.On<DeviceStateChangedMessage>("DeviceStateChanged", msg =>
            Dispatch(() => DeviceStateChanged?.Invoke(msg)));
        _connection.On<DeviceStateChangedMessage>("DeviceStateBroadcast", msg =>
            Dispatch(() => DeviceStateBroadcast?.Invoke(msg)));
        _connection.On<TaskStateChangedMessage>("TaskStateChanged", msg =>
            Dispatch(() => TaskStateChanged?.Invoke(msg)));
        _connection.On<AlarmEventMessage>("AlarmEvent", msg =>
            Dispatch(() => AlarmEvent?.Invoke(msg)));
        _connection.On<AlarmEventMessage>("AlarmBroadcast", msg =>
            Dispatch(() => AlarmBroadcast?.Invoke(msg)));
        _connection.On<ObjectMovedMessage>("ObjectMoved", msg =>
            Dispatch(() => ObjectMoved?.Invoke(msg)));
    }

    private void SetConnected(bool value)
    {
        _isConnected = value;
        Dispatch(() => ConnectionStateChanged?.Invoke(value));
    }

    private static void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
