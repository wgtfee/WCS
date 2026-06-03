namespace Wcs.Simulator.DeviceSimulator;

using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 堆垛机模拟器 — 模拟巷道水平/垂直/纵深三轴运动
/// </summary>
public class AsrsSimulator : DeviceSimulatorBase
{
    public AsrsSimulator(string deviceId, string deviceName, ISignalSource signalSource)
        : base(deviceId, deviceName, signalSource)
    {
        TransportTimeMs = 8000; // 堆垛机最慢
    }

    public override async Task StartAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await Task.Delay(TransportTimeMs, ct);
            if (!IsFaulted)
            {
                SimSignalSource.Emit($"{DeviceId}.StoreCompleted", true);
                await Task.Delay(500, ct);
                SimSignalSource.Emit($"{DeviceId}.StoreCompleted", false);
            }
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; }
    }

    /// <summary>模拟取货操作</summary>
    public async Task RetrieveAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            await Task.Delay(TransportTimeMs / 2, ct);
            if (!IsFaulted)
                SimSignalSource.Emit($"{DeviceId}.RetrieveCompleted", true);
        }
        finally { IsBusy = false; }
    }
}
