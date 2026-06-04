namespace Wcs.Core.PlcSubsystem.S7;

using Wcs.Core.PlcSubsystem.Pools;

/// <summary>DB 块注册 — 声明哪个 PLC 的哪个块对应哪个 C# struct</summary>
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
/// PLC 结构体注册表 — 管理所有 PLC 连接池和 DB 块映射
/// 使用 ReadPool（读）+ WritePool（写）双池架构
/// </summary>
public class PlcStructRegistry
{
    private readonly List<PlcBlockRegistration> _registrations = new();
    private readonly List<PlcBlockConfig> _pendingConfigs = new();

    /// <summary>读连接池（专用于读取 PLC）</summary>
    public ReadPool ReadPool { get; } = new();
    /// <summary>写连接池（专用于写入 PLC）</summary>
    public WritePool WritePool { get; } = new();

    /// <summary>获取或创建读连接</summary>
    public void AddReadConnection(string plcName, string address, int rack = 0, int slot = 0)
    {
        var conn = ReadPool.GetOrCreate(plcName, address, rack, slot);
        conn.Connect(out _);
    }

    /// <summary>获取或创建写连接</summary>
    public void AddWriteConnection(string plcName, string address, int rack = 0, int slot = 0)
    {
        var conn = WritePool.GetOrCreate(plcName, address, rack, slot);
        conn.Connect(out _);
    }

    /// <summary>代码注册 DB 块（泛型方式）</summary>
    public PlcBlockRegistration Register<T>(string plcName, int blockNumber,
        int length, int startByte = 0, int pollIntervalMs = 500) where T : class
    {
        var reg = new PlcBlockRegistration
        {
            PlcName = plcName, BlockNumber = blockNumber, StartByte = startByte,
            Length = length, PollIntervalMs = pollIntervalMs, StructType = typeof(T)
        };
        _registrations.Add(reg);
        return reg;
    }

    /// <summary>从配置项注册</summary>
    public PlcBlockRegistration RegisterFromConfig(PlcBlockConfig cfg)
    {
        var type = Type.GetType(cfg.StructType);
        if (type == null)
            throw new InvalidOperationException($"找不到类型 '{cfg.StructType}'，格式应为 \"完整命名空间.类型名, 程序集名\"");
        var reg = new PlcBlockRegistration
        {
            PlcName = cfg.PlcName, BlockNumber = cfg.BlockNumber,
            StartByte = 0, Length = cfg.Length,
            PollIntervalMs = cfg.PollIntervalMs, StructType = type
        };
        _registrations.Add(reg);
        return reg;
    }

    /// <summary>批量从配置注册（延迟到连接池就绪后执行）</summary>
    public void RegisterFromConfig(IEnumerable<PlcBlockConfig> configs)
    {
        foreach (var cfg in configs) RegisterFromConfig(cfg);
    }

    public IReadOnlyList<PlcBlockRegistration> GetAll() => _registrations;
    public IEnumerable<PlcBlockRegistration> GetByPlc(string plcName) =>
        _registrations.Where(r => r.PlcName == plcName);
}
