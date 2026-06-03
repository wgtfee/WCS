namespace Wcs.Simulator.PlcSimulator;

using System.Collections.Concurrent;

/// <summary>
/// 模拟 PLC 信号源 — 替代真实 S7 连接
/// 由设备模拟器注入信号，SignalMapper 完全无感知
/// </summary>
public class SimulatorSignalSource : ISignalSource
{
    private readonly ConcurrentQueue<SignalChangedEvent> _signalQueue = new();
    private readonly string _name;
    private bool _connected = true;

    public string Name => _name;
    public bool IsConnected => _connected;

    public SimulatorSignalSource(string name = "SimPLC")
    {
        _name = name;
    }

    /// <summary>
    /// 注入一个信号（由 DeviceSimulator 或 TransportGenerator 调用）
    /// </summary>
    public void Emit(string signalId, bool value = true, string? payload = null)
    {
        _signalQueue.Enqueue(new SignalChangedEvent
        {
            SignalId = signalId,
            Value = value,
            Payload = payload,
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// 模拟断线
    /// </summary>
    public void Disconnect() => _connected = false;

    /// <summary>
    /// 模拟恢复连接
    /// </summary>
    public void Reconnect() => _connected = true;

    public Task<IReadOnlyList<SignalChangedEvent>> ReadAsync(CancellationToken ct = default)
    {
        var batch = new List<SignalChangedEvent>();
        while (_signalQueue.TryDequeue(out var signal))
            batch.Add(signal);
        return Task.FromResult<IReadOnlyList<SignalChangedEvent>>(batch);
    }

    public Task WriteAsync(string signalId, bool value, CancellationToken ct = default)
    {
        // 模拟器端回写记录
        return Task.CompletedTask;
    }
}
