namespace Wcs.Core.PlcSubsystem.S7;

/// <summary>PLC 连接配置（对应 appsettings.json → PlcConnections）</summary>
public class PlcConnectionConfig
{
    public string PlcName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Rack { get; set; }
    public int Slot { get; set; }
    public int Timeout { get; set; } = 5000;
}

/// <summary>PLC DB 块配置（对应 appsettings.json → PlcBlocks）</summary>
public class PlcBlockConfig
{
    /// <summary>所属 PLC 名称（必须与 PlcConnections 中的 PlcName 匹配）</summary>
    public string PlcName { get; set; } = string.Empty;
    /// <summary>DB 块号</summary>
    public int BlockNumber { get; set; }
    /// <summary>读取长度（字节）</summary>
    public int Length { get; set; }
    /// <summary>轮询间隔（毫秒），慢变信号建议 1000~2000，快变信号 200~500</summary>
    public int PollIntervalMs { get; set; } = 500;
    /// <summary>
    /// C# struct 类型全名（含命名空间和程序集）
    /// 如 "Wcs.MyApp.DB1_Struct, Wcs.MyApp"
    /// 该类型必须与 Struct.FromBytes 兼容（按字段顺序映射 PLC DB 布局）
    /// </summary>
    public string StructType { get; set; } = string.Empty;
}
