namespace Wcs.Core.TransportScheduling;

public sealed record TransportDriverState
{
    public string VehicleId { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public string CurrentNodeId { get; init; } = string.Empty;
    public TransportVehicleOperatingState OperatingState { get; init; }
    public string? ActiveCommandId { get; init; }
    public long Sequence { get; init; }
    public int BatteryPercent { get; init; } = 100;
    public int FaultCode { get; init; }
    public string? FaultMessage { get; init; }
    public bool LoadPresent { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportDriverCommandResult
{
    public bool Accepted { get; init; }
    public bool Completed { get; init; }
    public string? Error { get; init; }

    public static TransportDriverCommandResult Ack(bool completed = false) => new() { Accepted = true, Completed = completed };
    public static TransportDriverCommandResult Reject(string error) => new() { Error = error };
}

public interface ITransportVehicleDriver
{
    TransportVehicleKind Kind { get; }
    Task<TransportDriverState> ReadStateAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<TransportDriverCommandResult> SendCommandAsync(TransportExecutionCommand command, CancellationToken cancellationToken = default);
}

public interface ITransportDriverResolver
{
    ITransportVehicleDriver Resolve(TransportVehicleKind kind);
}

public sealed class TransportDriverResolver : ITransportDriverResolver
{
    private readonly IReadOnlyDictionary<TransportVehicleKind, ITransportVehicleDriver> _drivers;

    public TransportDriverResolver(IEnumerable<ITransportVehicleDriver> drivers)
    {
        _drivers = drivers.GroupBy(x => x.Kind).ToDictionary(x => x.Key, x => x.Single());
    }

    public ITransportVehicleDriver Resolve(TransportVehicleKind kind) =>
        _drivers.TryGetValue(kind, out var driver)
            ? driver
            : throw new InvalidOperationException($"未注册 {kind} 车辆驱动");
}

/// <summary>用于 CI、离线调试与现场联调前验证的模拟驱动。</summary>
public sealed class SimulatorTransportVehicleDriver : ITransportVehicleDriver
{
    private readonly Dictionary<string, TransportDriverState> _states = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public SimulatorTransportVehicleDriver(TransportVehicleKind kind) => Kind = kind;
    public TransportVehicleKind Kind { get; }

    public Task<TransportDriverState> ReadStateAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_states.TryGetValue(vehicleId, out var state))
                return Task.FromResult(state);

            return Task.FromResult(new TransportDriverState
            {
                VehicleId = vehicleId,
                IsOnline = true,
                OperatingState = TransportVehicleOperatingState.Idle,
                BatteryPercent = 100
            });
        }
    }

    public Task<TransportDriverCommandResult> SendCommandAsync(TransportExecutionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var current = _states.GetValueOrDefault(command.VehicleId);
            _states[command.VehicleId] = new TransportDriverState
            {
                VehicleId = command.VehicleId,
                IsOnline = true,
                CurrentNodeId = command.TargetNodeId ?? current?.CurrentNodeId ?? string.Empty,
                OperatingState = TransportVehicleOperatingState.Executing,
                ActiveCommandId = command.CommandId,
                Sequence = (current?.Sequence ?? 0) + 1,
                BatteryPercent = current?.BatteryPercent ?? 100
            };
        }

        return Task.FromResult(TransportDriverCommandResult.Ack(completed: true));
    }
}
