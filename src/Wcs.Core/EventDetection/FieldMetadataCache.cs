namespace Wcs.Core.EventDetection;

using System.Collections.Concurrent;
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
}

/// <summary>
/// 字段元数据缓存 — 启动时按 Type 一次性反射，运行时只读缓存
///
/// 避免每 100ms 轮询时反复 GetFields / GetProperties 的反射开销。
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
                        DeviceId = deviceId
                    };
                })
                .ToArray();
        });
    }

    /// <summary>获取字段值</summary>
    public static object? GetValue(FieldMetadata meta, object instance) =>
        meta.FieldInfo.GetValue(instance);

    private static string? ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return null;
        var parts = fieldName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : null;
    }
}
