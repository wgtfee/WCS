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

/// <summary>
/// PLC 结构体注册表 — 从 JSON 配置自动加载，管理连接池和 DB 块映射
///
/// 配置驱动，不需要硬编码。appsettings.json 定义后自动注入。
/// </summary>
public class PlcStructRegistry
{
    private readonly List<PlcBlockRegistration> _registrations = new();
    private readonly Dictionary<string, S7PLCPool> _pools = new();

    /// <summary>获取或创建 S7 连接池</summary>
    public S7PLCPool GetOrCreatePool(string plcName, string address, int rack = 0, int slot = 0)
    {
        if (!_pools.ContainsKey(plcName))
        {
            var pool = S7PLCPool.GetInstance(plcName, address, rack, slot);
            pool.ConnectPLC(out _);
            _pools[plcName] = pool;
        }
        return _pools[plcName];
    }

    /// <summary>获取连接池</summary>
    public S7PLCPool? GetPool(string plcName) =>
        _pools.TryGetValue(plcName, out var p) ? p : null;

    public IEnumerable<S7PLCPool> GetAllPools() => _pools.Values;

    /// <summary>代码注册（泛型方式）</summary>
    public PlcBlockRegistration Register<T>(string plcName, int blockNumber,
        int length, int startByte = 0, int pollIntervalMs = 500) where T : class
    {
        var reg = new PlcBlockRegistration
        {
            PlcName = plcName, BlockNumber = blockNumber,
            StartByte = startByte, Length = length,
            PollIntervalMs = pollIntervalMs, StructType = typeof(T)
        };
        _registrations.Add(reg);
        return reg;
    }

    /// <summary>从 JSON 配置项注册（反射按类型名加载）</summary>
    public PlcBlockRegistration RegisterFromConfig(PlcBlockConfig cfg)
    {
        var type = Type.GetType(cfg.StructType);
        if (type == null)
            throw new InvalidOperationException(
                $"找不到类型 '{cfg.StructType}'。格式应为 \"完整命名空间.类型名, 程序集名\"，如 \"Wcs.MyApp.DB1_Struct, Wcs.MyApp\"");
        var reg = new PlcBlockRegistration
        {
            PlcName = cfg.PlcName, BlockNumber = cfg.BlockNumber,
            StartByte = 0, Length = cfg.Length,
            PollIntervalMs = cfg.PollIntervalMs, StructType = type
        };
        _registrations.Add(reg);
        return reg;
    }

    /// <summary>批量从 JSON 配置注册</summary>
    public void RegisterFromConfig(IEnumerable<PlcBlockConfig> configs)
    {
        foreach (var cfg in configs) RegisterFromConfig(cfg);
    }

    public IReadOnlyList<PlcBlockRegistration> GetAll() => _registrations;
    public IEnumerable<PlcBlockRegistration> GetByPlc(string plcName) =>
        _registrations.Where(r => r.PlcName == plcName);
}
