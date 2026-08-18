namespace Wcs.Core.TransportScheduling;

/// <summary>
/// 根据每辆车的配置在模拟驱动和 PLC 标签驱动之间切换。
/// 没有启用 PLC 映射时保持原有模拟行为，便于 CI 和离线开发。
/// </summary>
public sealed class SwitchableTransportVehicleDriver : ITransportVehicleDriver
{
    private readonly ITransportPlcSignalMapRegistry _maps;
    private readonly ReliableTransportVehicleDriver _reliable;
    private readonly SimulatorTransportVehicleDriver _simulator;

    public SwitchableTransportVehicleDriver(
        TransportVehicleKind kind,
        ITransportPlcSignalMapRegistry maps,
        ITransportDriverChannel channel)
    {
        Kind = kind;
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _reliable = new ReliableTransportVehicleDriver(
            kind,
            channel,
            new ReliableTransportVehicleDriverOptions
            {
                HeartbeatTimeout = TimeSpan.FromSeconds(30),
                CommandAcknowledgementTimeout = TimeSpan.FromSeconds(10),
                PollInterval = TimeSpan.FromMilliseconds(100)
            });
        _simulator = new SimulatorTransportVehicleDriver(kind);
    }

    public TransportVehicleKind Kind { get; }

    public Task<TransportDriverState> ReadStateAsync(
        string vehicleId,
        CancellationToken cancellationToken = default) =>
        Resolve(vehicleId).ReadStateAsync(vehicleId, cancellationToken);

    public Task<TransportDriverCommandResult> SendCommandAsync(
        TransportExecutionCommand command,
        CancellationToken cancellationToken = default) =>
        Resolve(command.VehicleId).SendCommandAsync(command, cancellationToken);

    private ITransportVehicleDriver Resolve(string vehicleId)
    {
        if (_maps.TryGet(vehicleId, out var map) &&
            map is { Enabled: true, Mode: TransportDriverMode.PlcTag } &&
            map.Kind == Kind)
        {
            return _reliable;
        }

        return _simulator;
    }
}
