namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// PLC 数据结构标记 — 类对应 PLC DB 块，属性名自动映射 PLC 变量名
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public class PlcStructAttribute : Attribute
{
    public string Path { get; }
    public int RefreshRateMs { get; init; } = 1000;
    public int TimeoutMs { get; init; } = 3000;

    public PlcStructAttribute(string path)
    {
        Path = path;
    }
}

/// <summary>
/// PLC 独立标签 — 字段单独读写，不归任何结构体
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public class PlcTagAttribute : Attribute
{
    public string Name { get; }
    public bool Monitored { get; init; } = true;

    public PlcTagAttribute(string name)
    {
        Name = name;
    }
}


[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class PlcIgnoreAttribute : Attribute { }