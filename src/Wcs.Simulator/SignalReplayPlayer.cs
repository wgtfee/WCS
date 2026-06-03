namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;
using System.Text.Json;
using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 信号回放播放器 — 重放真实 PLC 录制的信号日志
///
/// 回放日志格式（由 TraceCenter 或手动录制）：
/// [
///   { "time": "09:01:00", "signal": "CV01.Arrived", "value": true  },
///   { "time": "09:01:10", "signal": "Lift01.Ready",  "value": true  },
///   { "time": "09:01:20", "signal": "CV01.Arrived",  "value": false }
/// ]
///
/// 用途：100% 还原现场工况，用于回归测试
/// </summary>
public class SignalReplayPlayer
{
    private readonly SimulatorSignalSource _signalSource;
    private readonly ILogger<SignalReplayPlayer>? _logger;

    /// <summary>回放记录</summary>
    public record ReplaySignal
    {
        public string Time { get; init; } = string.Empty;
        public string Signal { get; init; } = string.Empty;
        public bool Value { get; init; }
        public string? Payload { get; init; }
    }

    /// <summary>回放结果</summary>
    public class ReplayResult
    {
        public int TotalSignals { get; set; }
        public int EmittedSignals { get; set; }
        public TimeSpan Duration { get; set; }
        public string? Error { get; set; }
    }

    public SignalReplayPlayer(SimulatorSignalSource signalSource,
        ILogger<SignalReplayPlayer>? logger = null)
    {
        _signalSource = signalSource;
        _logger = logger;
    }

    /// <summary>
    /// 从 JSON 文件回放信号
    /// </summary>
    public async Task<ReplayResult> PlayFileAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return await PlayJsonAsync(json, ct);
        }
        catch (Exception ex)
        {
            return new ReplayResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// 从 JSON 字符串回放信号
    /// </summary>
    public async Task<ReplayResult> PlayJsonAsync(string json, CancellationToken ct = default)
    {
        var signals = JsonSerializer.Deserialize<List<ReplaySignal>>(json);
        if (signals == null || signals.Count == 0)
            return new ReplayResult { Error = "No signals found" };

        var startTime = DateTime.UtcNow;
        var emitted = 0;
        DateTime? baseTime = null;

        foreach (var s in signals.OrderBy(s => s.Time))
        {
            if (ct.IsCancellationRequested) break;

            // 解析回放时间戳，计算等待间隔
            if (TimeSpan.TryParse(s.Time, out var offset))
            {
                baseTime ??= startTime;
                var targetTime = baseTime.Value.Add(offset);
                var delay = targetTime - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
            }

            _signalSource.Emit(s.Signal, s.Value, s.Payload);
            emitted++;
        }

        return new ReplayResult
        {
            TotalSignals = signals.Count,
            EmittedSignals = emitted,
            Duration = DateTime.UtcNow - startTime
        };
    }
}
