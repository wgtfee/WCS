namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;
using Wcs.Simulator.DeviceSimulator;
using Wcs.Simulator.PlcSimulator;

/// <summary>
/// 混沌猴子 — 故障注入工具
///
/// 随机注入故障，验证 WCS Core 的恢复能力：
/// - 设备断线/恢复
/// - 设备故障
/// - 信号延迟/丢失
/// - 负载突增
/// </summary>
public class ChaosMonkey
{
    private readonly List<DeviceSimulatorBase> _devices = new();
    private readonly SimulatorSignalSource? _signalSource;
    private readonly ILogger<ChaosMonkey>? _logger;
    private readonly Random _rng = new();
    private bool _running;

    public bool IsRunning => _running;

    /// <summary>故障注入概率（0~1，默认 0.05）</summary>
    public double FaultProbability { get; set; } = 0.05;

    public ChaosMonkey(SimulatorSignalSource? signalSource = null,
        ILogger<ChaosMonkey>? logger = null)
    {
        _signalSource = signalSource;
        _logger = logger;
    }

    public void RegisterDevice(DeviceSimulatorBase device)
    {
        _devices.Add(device);
    }

    /// <summary>
    /// 启动混沌猴子（随机注入故障）
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _running = true;
        _logger?.LogWarning("ChaosMonkey started: fault probability = {Pct}%", FaultProbability * 100);

        while (!ct.IsCancellationRequested && _running)
        {
            await Task.Delay(5000 + _rng.Next(15000), ct); // 随机间隔

            if (!_running) break;

            if (_rng.NextDouble() < FaultProbability && _devices.Count > 0)
            {
                var device = _devices[_rng.Next(_devices.Count)];
                var faultType = _rng.Next(3);

                switch (faultType)
                {
                    case 0: // 设备故障
                        device.InjectFault();
                        _logger?.LogWarning("Chaos: injected fault to {Device}", device.DeviceId);
                        // 随机恢复（5-30秒后）
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(_rng.Next(5000, 30000), ct);
                            device.Recover();
                            _logger?.LogInformation("Chaos: recovered {Device}", device.DeviceId);
                        }, ct);
                        break;

                    case 1: // PLC 断线
                        if (_signalSource is SimulatorSignalSource src)
                        {
                            src.Disconnect();
                            _logger?.LogWarning("Chaos: PLC disconnected");
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(_rng.Next(3000, 15000), ct);
                                src.Reconnect();
                                _logger?.LogInformation("Chaos: PLC reconnected");
                            }, ct);
                        }
                        break;

                    case 2: // 信号风暴（短时间内发射大量无用信号）
                        _logger?.LogWarning("Chaos: signal storm injected");
                        for (int i = 0; i < 100; i++)
                        {
                            _signalSource?.Emit($"Chaos.Noise_{i}", true);
                        }
                        break;
                }
            }
        }
    }

    public void Stop() => _running = false;
}
