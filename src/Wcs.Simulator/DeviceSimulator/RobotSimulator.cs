namespace Wcs.Simulator.DeviceSimulator;

using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 机器人模拟器 — 模拟抓取/放置操作
/// </summary>
public class RobotSimulator : DeviceSimulatorBase
{
    public RobotSimulator(string deviceId, string deviceName, ISignalSource signalSource)
        : base(deviceId, deviceName, signalSource)
    {
        TransportTimeMs = 2000; // 机器人最快
    }

    public override async Task StartAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await Task.Delay(TransportTimeMs, ct);
            if (!IsFaulted)
            {
                SimSignalSource.Emit($"{DeviceId}.GripCompleted", true);
                await Task.Delay(300, ct);
                SimSignalSource.Emit($"{DeviceId}.GripCompleted", false);
            }
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; }
    }

    /// <summary>模拟抓取</summary>
    public async Task GripAsync(string palletId, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await Task.Delay(1500, ct);
            if (!IsFaulted)
                SimSignalSource.Emit($"{DeviceId}.Gripped", true, $"{{\"pallet\":\"{palletId}\"}}");
        }
        finally { IsBusy = false; }
    }
}
