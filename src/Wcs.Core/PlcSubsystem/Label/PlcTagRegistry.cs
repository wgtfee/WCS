using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 标签定义 — 将标签名解析为 Snap7 可识别的 PLC 地址信息
/// </summary>
public class TagDefinition
{
    /// <summary>标签名（如 "DB1.CV01_DriveReady"）</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>DB 块号</summary>
    public int DbBlock { get; init; }

    /// <summary>字节偏移</summary>
    public int ByteOffset { get; init; }

    /// <summary>位偏移（-1=整个字节，0~7=指定位）</summary>
    public int BitOffset { get; init; } = -1;

    /// <summary>数据类型</summary>
    public Type DataType { get; init; } = typeof(byte);

    /// <summary>数组长度（仅数组类型时 > 0）</summary>
    public int ArrayLength { get; init; }

    /// <summary>所属 PLC 名称</summary>
    public string PlcName { get; init; } = string.Empty;
}

/// <summary>
/// PLC 标签注册表 — 管理标签名到(DB,偏移,类型)的映射
///
/// 功能：
/// 1. 手动注册：registry.Define("DB1.CV01_DriveReady", db:1, offset:0, bit:0, typeof(bool))
/// 2. 自动扫描：registry.Scan<T>() 从 [PlcStruct] + [PlcOffset] 特性扫描
/// 3. 批量注册：registry.RegisterBlock() 为一个 DB 块批量注册
/// </summary>
public class PlcTagRegistry
{
    private readonly ConcurrentDictionary<string, TagDefinition> _tags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>定义一个标签的地址映射</summary>
    public PlcTagRegistry Define(string name, int dbBlock, int byteOffset, int bitOffset, Type dataType, string? plcName = null)
    {
        _tags[name] = new TagDefinition
        {
            Name = name,
            DbBlock = dbBlock,
            ByteOffset = byteOffset,
            BitOffset = bitOffset,
            DataType = dataType,
            PlcName = plcName ?? string.Empty
        };
        return this;
    }

    /// <summary>定义一个标签（按字节，无位偏移）</summary>
    public PlcTagRegistry Define(string name, int dbBlock, int byteOffset, Type dataType, string? plcName = null)
        => Define(name, dbBlock, byteOffset, -1, dataType, plcName);

    /// <summary>批量定义标签</summary>
    public PlcTagRegistry DefineRange(IEnumerable<TagDefinition> definitions)
    {
        foreach (var def in definitions)
            _tags[def.Name] = def;
        return this;
    }

    /// <summary>查找标签定义</summary>
    public TagDefinition? Resolve(string tagName)
        => _tags.TryGetValue(tagName, out var def) ? def : null;

    /// <summary>判断标签是否已注册</summary>
    public bool Exists(string tagName) => _tags.ContainsKey(tagName);

    /// <summary>获取所有标签定义</summary>
    public IReadOnlyCollection<TagDefinition> GetAll() => _tags.Values.ToArray();

    /// <summary>获取指定 PLC 的所有标签</summary>
    public IEnumerable<TagDefinition> GetByPlc(string plcName)
        => _tags.Values.Where(t => t.PlcName == plcName);

    /// <summary>获取指定 DB 块的所有标签（按偏移排序）</summary>
    public IEnumerable<TagDefinition> GetByDbBlock(int dbBlock)
        => _tags.Values.Where(t => t.DbBlock == dbBlock).OrderBy(t => t.ByteOffset);

    /// <summary>
    /// 从 [PlcStruct] + [PlcTag] 标记的类型自动扫描注册标签。
    ///
    /// 注意：仅靠 [PlcTag("name")] 无法知道偏移量，需要额外提供偏移映射（通过约定或组合 [PlcOffset]）
    /// 此方法会优先读取 [PlcOffset] 属性，若没有则通过 DbBlock 和属性索引推断。
    /// </summary>
    public PlcTagRegistry ScanType(Type type, int dbBlock, string? plcName = null)
    {
        var structAttr = type.GetCustomAttribute<PlcStructAttribute>();
        var prefix = structAttr?.Path ?? type.Name;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<PlcIgnoreAttribute>() != null) continue;

            var tagAttr = prop.GetCustomAttribute<PlcTagAttribute>();
            var tagName = tagAttr?.Name ?? $"{prefix}.{prop.Name}";

            // 优先从 [PlcOffset] 读取地址
            var offsetAttr = prop.GetCustomAttribute<PlcOffsetAttribute>();
            if (offsetAttr != null)
            {
                Define(tagName, dbBlock, offsetAttr.ByteOffset, offsetAttr.BitOffset,
                    prop.PropertyType, plcName);
                continue;
            }

            // 没有偏移特性时，跳过（无法自动判断地址）
            // 调用方可以手动 Define()
        }

        // 同样处理字段
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.GetCustomAttribute<PlcIgnoreAttribute>() != null) continue;

            var tagAttr = field.GetCustomAttribute<PlcTagAttribute>();
            var tagName = tagAttr?.Name ?? $"{prefix}.{field.Name}";

            var offsetAttr = field.GetCustomAttribute<PlcOffsetAttribute>();
            if (offsetAttr != null)
            {
                Define(tagName, dbBlock, offsetAttr.ByteOffset, offsetAttr.BitOffset,
                    field.FieldType, plcName);
            }
        }

        return this;
    }

    /// <summary>泛型版本 ScanType</summary>
    public PlcTagRegistry ScanType<T>(int dbBlock, string? plcName = null)
        => ScanType(typeof(T), dbBlock, plcName);

    /// <summary>清空所有注册</summary>
    public void Clear() => _tags.Clear();

    /// <summary>注册数量</summary>
    public int Count => _tags.Count;
}
