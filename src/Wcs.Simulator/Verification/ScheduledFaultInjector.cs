namespace Wcs.Simulator.Verification;

using Wcs.Simulator.DeviceSimulator;
using Wcs.Simulator.PlcSimulator;

public enum SimulationFaultKind
{
    DeviceFault,
    DeviceRecover,
    PlcDisconnect,
    PlcReconnect,
    SignalBurst
}

/// <summary>
/// Deterministic fault scheduled at a scenario-relative offset.
/// </summary>
public sealed record ScheduledSimulationFault(
    TimeSpan Offset,
    SimulationFaultKind Kind,
    string? TargetDeviceId = null,
    int SignalCount = 100);

/// <summary>
/// Reproducible alternative to ChaosMonkey for CI and regression scenarios.
/// The same schedule always injects the same fault sequence at the same simulator time.
/// </summary>
public sealed class ScheduledFaultInjector
{
    private readonly SimulatorSignalSource _signalSource;
    private readonly IReadOnlyDictionary<string, DeviceSimulatorBase> _devices;
    private readonly ISimulationClock _clock;

    public ScheduledFaultInjector(
        SimulatorSignalSource signalSource,
        IEnumerable<DeviceSimulatorBase>? devices = null,
        ISimulationClock? clock = null)
    {
        _signalSource = signalSource ?? throw new ArgumentNullException(nameof(signalSource));
        _clock = clock ?? SystemSimulationClock.Instance;
        _devices = (devices ?? Array.Empty<DeviceSimulatorBase>())
            .ToDictionary(x => x.DeviceId, StringComparer.OrdinalIgnoreCase);
    }

    public event Action<ScheduledSimulationFault>? FaultInjected;

    public async Task RunAsync(
        IEnumerable<ScheduledSimulationFault> schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var ordered = schedule.OrderBy(x => x.Offset).ToArray();
        var elapsed = TimeSpan.Zero;

        foreach (var fault in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fault.Offset < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(schedule), "Fault offsets cannot be negative.");

            var wait = fault.Offset - elapsed;
            if (wait > TimeSpan.Zero)
                await _clock.DelayAsync(wait, cancellationToken);

            Apply(fault);
            elapsed = fault.Offset;
            FaultInjected?.Invoke(fault);
        }
    }

    private void Apply(ScheduledSimulationFault fault)
    {
        switch (fault.Kind)
        {
            case SimulationFaultKind.DeviceFault:
                ResolveDevice(fault).InjectFault();
                break;

            case SimulationFaultKind.DeviceRecover:
                ResolveDevice(fault).Recover();
                break;

            case SimulationFaultKind.PlcDisconnect:
                _signalSource.Disconnect();
                break;

            case SimulationFaultKind.PlcReconnect:
                _signalSource.Reconnect();
                break;

            case SimulationFaultKind.SignalBurst:
                if (fault.SignalCount <= 0)
                    throw new ArgumentOutOfRangeException(nameof(fault), "SignalCount must be greater than zero.");

                for (var i = 0; i < fault.SignalCount; i++)
                    _signalSource.Emit($"Verification.Noise_{i}", true);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault.Kind, "Unknown simulation fault kind.");
        }
    }

    private DeviceSimulatorBase ResolveDevice(ScheduledSimulationFault fault)
    {
        if (string.IsNullOrWhiteSpace(fault.TargetDeviceId))
            throw new InvalidOperationException($"Fault '{fault.Kind}' requires TargetDeviceId.");

        if (!_devices.TryGetValue(fault.TargetDeviceId, out var device))
            throw new KeyNotFoundException($"Simulation device '{fault.TargetDeviceId}' is not registered.");

        return device;
    }
}
