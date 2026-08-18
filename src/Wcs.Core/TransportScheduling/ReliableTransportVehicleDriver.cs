namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public sealed record TransportProtocolCommandFrame
{
    public string CommandId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public TransportExecutionCommandType CommandType { get; init; }
    public string? TargetNodeId { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportProtocolStateFrame
{
    public string VehicleId { get; init; } = string.Empty;
    public bool DeviceOnline { get; init; }
    public string CurrentNodeId { get; init; } = string.Empty;
    public TransportVehicleOperatingState OperatingState { get; init; }
    public string? ActiveCommandId { get; init; }
    public long StateSequence { get; init; }
    public string? AcknowledgedCommandId { get; init; }
    public long AcknowledgedSequence { get; init; }
    public bool CommandAccepted { get; init; }
    public bool CommandCompleted { get; init; }
    public string? CommandError { get; init; }
    public int BatteryPercent { get; init; } = 100;
    public int FaultCode { get; init; }
    public string? FaultMessage { get; init; }
    public bool LoadPresent { get; init; }
    public DateTime HeartbeatAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ITransportDriverChannel
{
    Task WriteCommandAsync(TransportProtocolCommandFrame command, CancellationToken cancellationToken = default);
    Task<TransportProtocolStateFrame> ReadStateAsync(string vehicleId, CancellationToken cancellationToken = default);
}

public sealed record ReliableTransportVehicleDriverOptions
{
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CommandAcknowledgementTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// 面向真实 EMS/RGV 协议适配器的可靠驱动。
/// 底层通道负责 PLC、TCP 或厂商控制器的具体地址映射；本类统一处理命令序号、幂等确认、
/// 心跳超时和确认超时，避免调度层直接依赖具体协议。
/// </summary>
public sealed class ReliableTransportVehicleDriver : ITransportVehicleDriver
{
    private readonly ITransportDriverChannel _channel;
    private readonly ReliableTransportVehicleDriverOptions _options;
    private readonly ConcurrentDictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vehicleGates = new(StringComparer.Ordinal);

    public ReliableTransportVehicleDriver(
        TransportVehicleKind kind,
        ITransportDriverChannel channel,
        ReliableTransportVehicleDriverOptions? options = null)
    {
        Kind = kind;
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _options = options ?? new ReliableTransportVehicleDriverOptions();
        Validate(_options);
    }

    public TransportVehicleKind Kind { get; }

    public async Task<TransportDriverState> ReadStateAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        var state = await _channel.ReadStateAsync(vehicleId, cancellationToken).ConfigureAwait(false);
        var heartbeatAlive = DateTime.UtcNow - state.HeartbeatAtUtc <= _options.HeartbeatTimeout;
        var online = state.DeviceOnline && heartbeatAlive;

        return new TransportDriverState
        {
            VehicleId = vehicleId,
            IsOnline = online,
            CurrentNodeId = state.CurrentNodeId,
            OperatingState = online ? state.OperatingState : TransportVehicleOperatingState.Offline,
            ActiveCommandId = state.ActiveCommandId,
            Sequence = state.StateSequence,
            BatteryPercent = state.BatteryPercent,
            FaultCode = state.FaultCode,
            FaultMessage = state.FaultMessage ?? state.CommandError,
            LoadPresent = state.LoadPresent,
            UpdatedAtUtc = state.HeartbeatAtUtc
        };
    }

    public async Task<TransportDriverCommandResult> SendCommandAsync(
        TransportExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var gate = _vehicleGates.GetOrAdd(command.VehicleId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingState = await _channel.ReadStateAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
            var existing = ResolveAcknowledgement(existingState, command.CommandId, 0);
            if (existing is not null)
                return existing;

            if (!existingState.DeviceOnline ||
                DateTime.UtcNow - existingState.HeartbeatAtUtc > _options.HeartbeatTimeout)
            {
                throw new InvalidOperationException($"车辆 {command.VehicleId} 离线或心跳超时，禁止下发命令");
            }

            var sequence = _sequences.AddOrUpdate(
                command.VehicleId,
                _ => Math.Max(1, existingState.AcknowledgedSequence + 1),
                (_, current) => Math.Max(current + 1, existingState.AcknowledgedSequence + 1));

            var frame = new TransportProtocolCommandFrame
            {
                CommandId = command.CommandId,
                RequestId = command.RequestId,
                VehicleId = command.VehicleId,
                Sequence = sequence,
                CommandType = command.CommandType,
                TargetNodeId = command.TargetNodeId,
                CreatedAtUtc = command.CreatedAtUtc
            };

            await _channel.WriteCommandAsync(frame, cancellationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow.Add(_options.CommandAcknowledgementTimeout);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await _channel.ReadStateAsync(command.VehicleId, cancellationToken).ConfigureAwait(false);
                var acknowledgement = ResolveAcknowledgement(state, command.CommandId, sequence);
                if (acknowledgement is not null)
                    return acknowledgement;

                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"车辆 {command.VehicleId} 命令 {command.CommandId} 在 {_options.CommandAcknowledgementTimeout.TotalMilliseconds:0}ms 内未确认");
        }
        finally
        {
            gate.Release();
        }
    }

    private static TransportDriverCommandResult? ResolveAcknowledgement(
        TransportProtocolStateFrame state,
        string commandId,
        long expectedSequence)
    {
        if (!string.Equals(state.AcknowledgedCommandId, commandId, StringComparison.Ordinal))
            return null;
        if (expectedSequence > 0 && state.AcknowledgedSequence < expectedSequence)
            return null;

        return state.CommandAccepted
            ? TransportDriverCommandResult.Ack(state.CommandCompleted)
            : TransportDriverCommandResult.Reject(state.CommandError ?? "设备拒绝命令");
    }

    private static void Validate(ReliableTransportVehicleDriverOptions options)
    {
        if (options.HeartbeatTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "HeartbeatTimeout 必须大于 0");
        if (options.CommandAcknowledgementTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "CommandAcknowledgementTimeout 必须大于 0");
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval 必须大于 0");
    }
}

/// <summary>供 CI 和协议联调使用的可控通道，不代表现场 PLC 实现。</summary>
public sealed class InMemoryTransportDriverChannel : ITransportDriverChannel
{
    private readonly ConcurrentDictionary<string, TransportProtocolStateFrame> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransportProtocolCommandFrame> _commands = new(StringComparer.Ordinal);

    public bool AutoAcknowledge { get; set; } = true;
    public bool CompleteOnAcknowledge { get; set; } = true;

    public Task WriteCommandAsync(TransportProtocolCommandFrame command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _commands[command.VehicleId] = command;

        if (AutoAcknowledge)
        {
            var current = _states.GetValueOrDefault(command.VehicleId) ?? new TransportProtocolStateFrame
            {
                VehicleId = command.VehicleId,
                DeviceOnline = true,
                OperatingState = TransportVehicleOperatingState.Idle
            };
            _states[command.VehicleId] = current with
            {
                ActiveCommandId = command.CommandId,
                AcknowledgedCommandId = command.CommandId,
                AcknowledgedSequence = command.Sequence,
                CommandAccepted = true,
                CommandCompleted = CompleteOnAcknowledge,
                StateSequence = current.StateSequence + 1,
                HeartbeatAtUtc = DateTime.UtcNow
            };
        }

        return Task.CompletedTask;
    }

    public Task<TransportProtocolStateFrame> ReadStateAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _states.GetValueOrDefault(vehicleId) ?? new TransportProtocolStateFrame
        {
            VehicleId = vehicleId,
            DeviceOnline = true,
            OperatingState = TransportVehicleOperatingState.Idle,
            BatteryPercent = 100,
            HeartbeatAtUtc = DateTime.UtcNow
        };
        return Task.FromResult(state);
    }

    public void SetState(TransportProtocolStateFrame state) => _states[state.VehicleId] = state;
    public TransportProtocolCommandFrame? GetLastCommand(string vehicleId) => _commands.GetValueOrDefault(vehicleId);
}
