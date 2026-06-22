using Microsoft.Extensions.Logging;
using MQTTnet;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.Mqtt;

/// <summary>
/// MQTT 连接配置
/// </summary>
public class MqttConnectionConfig
{
    public string Name { get; set; } = "MqttBroker";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = $"WcsCore_{Guid.NewGuid():N}";
    public int TimeoutMs { get; set; } = 5000;
}

/// <summary>
/// MQTT 连接实现 — 基于 MQTTnet 库
/// 注意：MQTT 是发布/订阅模型，Read 需配合 Subscribe 缓存实现
/// </summary>
public class MqttConnection : PlcConnectionBase
{
    private readonly MqttConnectionConfig _config;
    private IMqttClient? _client;

    /// <summary>收到消息时触发</summary>
    public event Func<string, byte[], Task>? OnMessageReceived;

    public override PlcProtocolType ProtocolType => PlcProtocolType.Mqtt;
    public IMqttClient? Client => _client;
    public MqttClientOptions? Options { get; private set; }

    public MqttConnection(MqttConnectionConfig config, ILogger<MqttConnection> logger)
        : base(config.Name, logger)
    {
        _config = config;
        Status.ProtocolType = ProtocolType;
    }

    public override async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (Connected) return true;
        SetStatus(PlcConnectionStatusEnum.Connecting);

        try
        {
            var factory = new MqttClientFactory();

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.Host, _config.Port)
                .WithClientId(_config.ClientId)
                .WithTimeout(TimeSpan.FromMilliseconds(_config.TimeoutMs));

            if (!string.IsNullOrEmpty(_config.Username))
                optionsBuilder.WithCredentials(_config.Username, _config.Password!);

            Options = optionsBuilder.Build();
            _client = factory.CreateMqttClient();

            _client.ConnectedAsync += OnConnectedAsync;
            _client.DisconnectedAsync += OnDisconnectedAsync;
            _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceived;

            var result = await _client.ConnectAsync(Options, ct);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                Connected = true;
                SetStatus(PlcConnectionStatusEnum.Connected);
                Logger.LogInformation("MQTT [{Name}] connected to {Host}:{Port}",
                    Name, _config.Host, _config.Port);
                return true;
            }

            SetStatus(PlcConnectionStatusEnum.Failed, result.ResultCode.ToString());
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            Logger.LogError(ex, "MQTT [{Name}] connect failed", Name);
            return false;
        }
    }

    public override async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        if (!Connected) return true;
        SetStatus(PlcConnectionStatusEnum.Disconnecting);

        try
        {
            if (_client != null)
            {
                var disconnectOptions = new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build();
                await _client.DisconnectAsync(disconnectOptions, ct);
                _client.Dispose();
            }
            Connected = false;
            SetStatus(PlcConnectionStatusEnum.Disconnected);
            Logger.LogInformation("MQTT [{Name}] disconnected", Name);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(PlcConnectionStatusEnum.Failed, ex.Message);
            return false;
        }
    }

    public override async Task<bool> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        if (_client == null || !Connected) return false;

        try
        {
            var builder = new MqttApplicationMessageBuilder()
                .WithTopic(address)
                .WithPayload(data);

            var result = await _client.PublishAsync(builder.Build(), ct);
            if (result.IsSuccess) CountWrite();
            return result.IsSuccess;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MQTT [{Name}] publish {Topic} failed", Name, address);
            return false;
        }
    }

    public override Task<byte[]?> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        Logger.LogWarning("MQTT [{Name}] direct Read not supported, use Subscribe + OnMessageReceived", Name);
        return Task.FromResult<byte[]?>(null);
    }

    public async Task<bool> SubscribeAsync(string topic)
    {
        if (_client == null || !Connected) return false;

        try
        {
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(t => t.WithTopic(topic))
                .Build();
            await _client.SubscribeAsync(subscribeOptions);
            Logger.LogInformation("MQTT [{Name}] subscribed to {Topic}", Name, topic);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MQTT [{Name}] subscribe {Topic} failed", Name, topic);
            return false;
        }
    }

    public async Task<bool> UnsubscribeAsync(string topic)
    {
        if (_client == null || !Connected) return false;
        try
        {
            await _client.UnsubscribeAsync(topic);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MQTT [{Name}] unsubscribe {Topic} failed", Name, topic);
            return false;
        }
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        Logger.LogInformation("MQTT [{Name}] connected event received", Name);
        return Task.CompletedTask;
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        Logger.LogWarning("MQTT [{Name}] disconnected, reason: {Reason}", Name, args.Reason);
        Connected = false;
        SetStatus(PlcConnectionStatusEnum.Disconnected, args.Reason.ToString());

        if (args.Reason != MqttClientDisconnectReason.NormalDisconnection)
        {
            await Task.Delay(3000);
            _ = ConnectAsync();
        }
    }

    private Task OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        CountRead();
        var topic = args.ApplicationMessage.Topic;
        var buffer = args.ApplicationMessage.Payload;
        byte[] payload;
        if (buffer.IsSingleSegment)
        {
            payload = buffer.First.Span.ToArray();
        }
        else
        {
            payload = new byte[buffer.Length];
            var pos = 0;
            foreach (var segment in buffer)
            {
                segment.Span.CopyTo(payload.AsSpan(pos));
                pos += segment.Length;
            }
        }
        Logger.LogDebug("MQTT [{Name}] received on {Topic}, len={Len}", Name, topic, payload.Length);
        _ = OnMessageReceived?.Invoke(topic, payload);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _client?.Dispose();
        base.Dispose();
    }
}
