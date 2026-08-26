namespace Wcs.Core.EventDetection;

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

/// <summary>
/// 字段元数据 — 单次反射解析，运行时零反射
/// </summary>
public class FieldMetadata
{
    public string FieldName { get; set; } = string.Empty;
    public FieldInfo FieldInfo { get; set; } = null!;
    public string Suffix { get; set; } = string.Empty;
    public string? DeviceId { get; set; }

    /// <summary>
    /// 表达式树编译的字段读取委托。
    /// 值类型字段通过 Unbox 直接寻址，避免 FieldInfo.GetValue 的装箱与反射调用开销。
    /// </summary>
    public Func<object, object?> Getter { get; set; } = null!;
}

/// <summary>
/// 字段元数据缓存 — 启动时按 Type 一次性反射，运行时只读缓存
///
/// 避免每 100ms 轮询时反复 GetFields / GetProperties 的反射开销；
/// 字段读取使用编译委托，热路径（边沿检测/状态同步）不再逐字段装箱调用 FieldInfo.GetValue。
/// </summary>
public static class FieldMetadataCache
{
    private static readonly ConcurrentDictionary<Type, FieldMetadata[]> _cache = new();

    /// <summary>
    /// 获取指定 struct 类型的元数据（首次使用时反射缓存）
    /// </summary>
    public static FieldMetadata[] GetFields(Type structType)
    {
        return _cache.GetOrAdd(structType, type =>
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f =>
                {
                    var name = f.Name.ToUpperInvariant();
                    var suffix = "";
                    var lastUnderscore = name.LastIndexOf('_');
                    if (lastUnderscore >= 0)
                        suffix = name.Substring(lastUnderscore);

                    var deviceId = ExtractDeviceId(f.Name);

                    return new FieldMetadata
                    {
                        FieldName = f.Name,
                        FieldInfo = f,
                        Suffix = suffix,
                        DeviceId = deviceId,
                        Getter = CompileGetter(f)
                    };
                })
                .ToArray();
        });
    }

    /// <summary>获取字段值（编译委托，无反射调用开销）</summary>
    public static object? GetValue(FieldMetadata meta, object instance) =>
        meta.Getter(instance);

    private static Func<object, object?> CompileGetter(FieldInfo field)
    {
        var declaringType = field.DeclaringType ?? throw new InvalidOperationException(
            $"字段 {field.Name} 没有声明类型");

        var instanceParameter = Expression.Parameter(typeof(object), "instance");

        // struct 实例以 object 形式传入，先 Unbox 得到类型化引用再取字段，
        // 避免对整个结构体的额外拷贝；class 实例直接 Convert。
        Expression typedInstance = declaringType.IsValueType
            ? Expression.Unbox(instanceParameter, declaringType)
            : Expression.Convert(instanceParameter, declaringType);

        var body = Expression.Convert(Expression.Field(typedInstance, field), typeof(object));

        return Expression.Lambda<Func<object, object?>>(body, instanceParameter).Compile();
    }

    private static string? ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return null;
        var parts = fieldName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : null;
    }
}
