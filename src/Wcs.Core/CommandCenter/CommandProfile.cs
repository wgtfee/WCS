namespace Wcs.Core.CommandCenter;

/// <summary>
/// 命令状态机配置模板 — 适配不同 PLC 的反馈能力
///
/// 西门子现场：
///   输送线：Sent → Executing → Completed（无 Ack, 无 Done）
///   堆垛机：Sent → Acked → Executing → Done → Completed（完整五态）
///   提升机：Sent → Executing → Completed（有的只有 Busy 信号）
///
/// 用法：
///   输送线:
///     new CommandProfile { HasAck = false, HasBusy = true, HasDone = false }
///   堆垛机:
///     new CommandProfile { HasAck = true, HasBusy = true, HasDone = true }
///   简单 IO:
///     new CommandProfile { HasAck = false, HasBusy = false, HasDone = false }
/// </summary>
public class CommandProfile
{
    /// <summary>PLC 是否会置 ACK 位确认收到命令（默认 false）</summary>
    public bool HasAck { get; set; } = false;

    /// <summary>PLC 是否会置 Busy 位表示正在执行（默认 true）</summary>
    public bool HasBusy { get; set; } = true;

    /// <summary>PLC 是否会置 Done 位表示执行完成（默认 false）</summary>
    public bool HasDone { get; set; } = false;

    /// <summary>命令超时时间（毫秒）</summary>
    public int TimeoutMs { get; set; } = 10000;

    /// <summary>设备 ID → CommandProfile 映射</summary>
    private static readonly Dictionary<string, CommandProfile> Defaults = new()
    {
        ["CV01"] = new() { HasAck = false, HasBusy = true, HasDone = false, TimeoutMs = 5000 },
        ["CV02"] = new() { HasAck = false, HasBusy = true, HasDone = false, TimeoutMs = 5000 },
        ["LIFT01"] = new() { HasAck = false, HasBusy = true, HasDone = true, TimeoutMs = 15000 },
        ["ASRS01"] = new() { HasAck = true, HasBusy = true, HasDone = true, TimeoutMs = 30000 },
        ["ROBOT01"] = new() { HasAck = true, HasBusy = true, HasDone = true, TimeoutMs = 10000 },
    };

    /// <summary>获取设备默认配置，未配置的设备使用通用默认值</summary>
    public static CommandProfile ForDevice(string deviceId)
    {
        return Defaults.TryGetValue(deviceId, out var profile) ? profile : new CommandProfile();
    }
}
