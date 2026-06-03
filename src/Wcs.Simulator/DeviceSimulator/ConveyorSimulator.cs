namespace Wcs.Simulator.DeviceSimulator;

using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 输送线模拟器 — 收到命令后模拟输送时间，发出到位信号
/// </summary>
public class ConveyorSimulator : DeviceSimulatorBase
{
    public ConveyorSimulator(string deviceId, string deviceName, ISignalSource signalSource)
        : base(deviceId, deviceName, signalSource) { }

    public override async Task StartAsync(CancellationToken ct = default)
    {
        IsBusy = true;

        try
        {
            // 模拟输送线启动时间
            await Task.Delay(TransportTimeMs, ct);

            if (!IsFaulted)
            {
                // 发出到位信号（会被 SignalMapper 接收）
                SimSignalSource.Emit($"{DeviceId}.Arrived", true, $"{{\"device\":\"{DeviceId}\"}}");

                // 等待短暂后清除信号
                await Task.Delay(500, ct);
                SimSignalSource.Emit($"{DeviceId}.Arrived", false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 模拟托盘从该输送线离开
    /// </summary>
    public void EmitDeparture(string palletId)
    {
        SimSignalSource.Emit($"{DeviceId}.Departed", true, $"{{\"pallet\":\"{palletId}\"}}");
    }
}
