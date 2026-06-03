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
                        _logger?.LogWarning("[S7] {Plc} 无连接池", reg.PlcName);
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

                    // 使用反射调用泛型 ProcessAsync<T>，替代 dynamic（DLR 运行时脆弱）
                    var result2 = await _bridge.ProcessUntypedAsync(
                        blockKey, reg.StructType, reg.PreviousStruct, current);

                    if (result2.AcceptedChanges > 0)
                        _logger?.LogDebug("[S7] {Block}: {Accepted} 字段变化已处理",
                            blockKey, result2.AcceptedChanges);

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
