namespace Wcs.Simulator.DeviceSimulator;

using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 提升机模拟器 — 模拟垂直输送时间
/// </summary>
public class LiftSimulator : DeviceSimulatorBase
{
    public LiftSimulator(string deviceId, string deviceName, ISignalSource signalSource)
        : base(deviceId, deviceName, signalSource)
    {
        TransportTimeMs = 5000; // 提升机默认更慢
    }

    public override async Task StartAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await Task.Delay(TransportTimeMs, ct);
            if (!IsFaulted)
            {
                SimSignalSource.Emit($"{DeviceId}.Arrived", true);
                await Task.Delay(500, ct);
                SimSignalSource.Emit($"{DeviceId}.Arrived", false);
                SimSignalSource.Emit($"{DeviceId}.Ready", true);
            }
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; }
    }

    /// <summary>模拟移动到指定楼层</summary>
    public async Task MoveToFloor(int floor, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await Task.Delay(floor * 1000, ct);
            if (!IsFaulted)
                SimSignalSource.Emit($"{DeviceId}.FloorReached", true, $"{{\"floor\":{floor}}}");
        }
        finally { IsBusy = false; }
    }
}
