namespace Wcs.Simulator.PlcSimulator;

/// <summary>
/// 信号变化事件 — 模拟 PLC 发出的业务信号
/// </summary>
public record SignalChangedEvent
{
    /// <summary>信号 ID（如 "CV01.Arrived", "Lift01.Ready"）</summary>
    public string SignalId { get; init; } = string.Empty;
    /// <summary>信号值</summary>
    public bool Value { get; init; }
    /// <summary>附加数据</summary>
    public string? Payload { get; init; }
    /// <summary>信号时间</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 信号源接口 — 抽象真实 PLC 和模拟 PLC
/// 真实 PLC：PlcSignalSource（通过 S7Connection 读取）
/// 模拟 PLC：SimulatorSignalSource（由设备模拟器或回放驱动）
/// </summary>
public interface ISignalSource
{
    /// <summary>信号源名称</summary>
    string Name { get; }
    /// <summary>读取所有待处理的信号变化</summary>
    Task<IReadOnlyList<SignalChangedEvent>> ReadAsync(CancellationToken ct = default);
    /// <summary>向信号源写入控制信号（模拟器回写设备反馈）</summary>
    Task WriteAsync(string signalId, bool value, CancellationToken ct = default);
    /// <summary>信号源是否已连接</summary>
    bool IsConnected { get; }
}
