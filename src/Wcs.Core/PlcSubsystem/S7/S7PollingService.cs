namespace Wcs.Core.PlcSubsystem.S7;

using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem.SignalMapper.S7;

public class S7PollingService
{
    private readonly PlcStructRegistry _registry;
    private readonly StructBridge _bridge;
    private readonly ILogger<S7PollingService>? _logger;
    private readonly List<Timer> _timers = new();
    private bool _running;

    public S7PollingService(PlcStructRegistry registry, StructBridge bridge, ILogger<S7PollingService>? logger = null)
    {
        _registry = registry;
        _bridge = bridge;
        _logger = logger;
    }

    /// <summary>启动所有 PLC 所有 DB 块的轮询</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;

        foreach (var reg in _registry.GetAll())
        {
            var timer = new Timer(async _ =>
            {
                try
                {
                    var pool = _registry.GetPool(reg.PlcName);
                    if (pool == null)
                    {
                        _logger?.LogWarning("[S7] {Plc} 无连接池，跳过 DB{Block}", reg.PlcName, reg.BlockNumber);
                        return;
                    }

                    var (data, result, error) = await pool.ReadPLCDataAsync(
                        reg.BlockNumber, 0, reg.Length);

                    if (result != 0 || data == null || data.Length == 0)
                    {
                        _logger?.LogWarning("[S7] {Plc} DB{Block} 读取失败: {Error}",
                            reg.PlcName, reg.BlockNumber, error);
                        return;
                    }

                    var blockKey = $"{reg.PlcName}.DB{reg.BlockNumber}";
                    var current = Struct.FromBytes(reg.StructType, data, reg.Length, 0);
                    if (current == null) return;

                    _bridge.Process(blockKey, reg.PreviousStruct as dynamic, current as dynamic);
                    reg.PreviousStruct = current;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[S7] {Plc} DB{Block}", reg.PlcName, reg.BlockNumber);
                }
            }, null, 0, reg.PollIntervalMs);

            _timers.Add(timer);
            _logger?.LogInformation("[S7] 轮询: {Plc} DB{Block} ({Length}B @ {Interval}ms)",
                reg.PlcName, reg.BlockNumber, reg.Length, reg.PollIntervalMs);
        }
    }

    public void Stop()
    {
        _running = false;
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }
}
