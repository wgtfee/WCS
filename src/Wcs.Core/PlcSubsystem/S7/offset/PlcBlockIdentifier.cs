namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// PLC 块标识 — 一个设备对应到具体的 PLC、DB 块、起始偏移、结构体类型
///
/// 用于"设备 ID → 读哪个 PLC 的哪个 DB 块"的映射查询。
///
/// 示例：
///   CV01 → PLC1, DB1, Offset 0, DB1_Struct
///   LIFT01 → PLC1, DB2, Offset 0, DB2_Struct
///   ASRS01 → PLC2, DB10, Offset 0, ASRS_Struct
/// </summary>
public class PlcBlockIdentifier
{
    /// <summary>设备 ID（如 CV01, LIFT01, ASRS01）</summary>
    public string DeviceId { get; set; } = string.Empty;
    /// <summary>PLC 名称（对应 PlcConnections 中的 PlcName）</summary>
    public string PlcName { get; set; } = string.Empty;
    /// <summary>DB 块号</summary>
    public int DbBlock { get; set; }
    /// <summary>起始字节偏移</summary>
    public int StartByte { get; set; }
    /// <summary>读取长度（字节）</summary>
    public int Length { get; set; }
    /// <summary>对应的 C# struct 类型全名（用于 PlcSerializer 反序列化）</summary>
    public string? StructType { get; set; }
}

/// <summary>
/// PLC 块识别器 — 管理设备 ID → PLC/DB 块的映射
///
/// 让系统知道：想读 CV01 的状态时，该去哪个 PLC 的哪个 DB 块找数据
/// </summary>
public class PlcBlockRegistry
{
    private readonly Dictionary<string, PlcBlockIdentifier> _mappings = new();
    private readonly object _lock = new();

    /// <summary>注册一个设备映射</summary>
    public void Register(PlcBlockIdentifier id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_lock) { _mappings[id.DeviceId] = id; }
    }

    /// <summary>批量注册</summary>
    public void RegisterRange(IEnumerable<PlcBlockIdentifier> ids)
    {
        lock (_lock) { foreach (var id in ids) _mappings[id.DeviceId] = id; }
    }

    /// <summary>查询设备对应的 PLC 块信息</summary>
    public PlcBlockIdentifier? Get(string deviceId)
    {
        lock (_lock) { return _mappings.TryGetValue(deviceId, out var id) ? id : null; }
    }

    /// <summary>获取所有已注册的设备 ID</summary>
    public IEnumerable<string> GetAllDeviceIds()
    {
        lock (_lock) { return _mappings.Keys.ToList(); }
    }

    /// <summary>获取所有注册项</summary>
    public IEnumerable<PlcBlockIdentifier> GetAll()
    {
        lock (_lock) { return _mappings.Values.ToList(); }
    }
}
