namespace Wcs.Core.PlcSubsystem.S7;

using Wcs.Core.PlcSubsystem.SignalMapper.S7;

public class PlcBlockRegistration
{
    public string PlcName { get; set; } = string.Empty;
    public int BlockNumber { get; set; }
    public int StartByte { get; set; }
    public int Length { get; set; }
    public int PollIntervalMs { get; set; } = 500;
    public Type StructType { get; set; } = null!;
    public object? PreviousStruct { get; set; }
}

public class PlcStructRegistry
{
    private readonly List<PlcBlockRegistration> _registrations = new();
    private readonly Dictionary<string, S7PLCPool> _pools = new();
    private readonly Dictionary<string, string> _poolErrors = new();

    /// <summary>获取或创建连接池（连接失败不阻塞，错误记录在 GetPoolError）</summary>
    public S7PLCPool GetOrCreatePool(string plcName, string address, int rack = 0, int slot = 0)
    {
        if (!_pools.ContainsKey(plcName))
        {
            var pool = S7PLCPool.GetInstance(plcName, address, rack, slot);
            var result = pool.ConnectPLC(out var error);
            _pools[plcName] = pool;
            if (result != 0) _poolErrors[plcName] = error;
        }
        return _pools[plcName];
    }

    public S7PLCPool? GetPool(string plcName) => _pools.TryGetValue(plcName, out var p) ? p : null;
    public string? GetPoolError(string plcName) => _poolErrors.TryGetValue(plcName, out var e) ? e : null;
    public IEnumerable<S7PLCPool> GetAllPools() => _pools.Values;

    public PlcBlockRegistration Register<T>(string plcName, int blockNumber,
        int length, int startByte = 0, int pollIntervalMs = 500) where T : class
    {
        var reg = new PlcBlockRegistration { PlcName = plcName, BlockNumber = blockNumber, StartByte = startByte, Length = length, PollIntervalMs = pollIntervalMs, StructType = typeof(T) };
        _registrations.Add(reg);
        return reg;
    }

    public PlcBlockRegistration RegisterFromConfig(PlcBlockConfig cfg)
    {
        var type = Type.GetType(cfg.StructType);
        if (type == null) throw new InvalidOperationException($"类型 '{cfg.StructType}' 未找到。格式: \"完整命名空间.类型名, 程序集名\"");
        var reg = new PlcBlockRegistration { PlcName = cfg.PlcName, BlockNumber = cfg.BlockNumber, StartByte = 0, Length = cfg.Length, PollIntervalMs = cfg.PollIntervalMs, StructType = type };
        _registrations.Add(reg);
        return reg;
    }

    public void RegisterFromConfig(IEnumerable<PlcBlockConfig> configs) { foreach (var cfg in configs) RegisterFromConfig(cfg); }
    public IReadOnlyList<PlcBlockRegistration> GetAll() => _registrations;
    public IEnumerable<PlcBlockRegistration> GetByPlc(string plcName) => _registrations.Where(r => r.PlcName == plcName);
}
