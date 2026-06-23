using System.Reflection;
using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.PlcSubsystem.S7.S7CommPlus;

namespace Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 基于标签特性的序列化器 — 使用 [PlcStruct] / [PlcTag] 特性读写 PLC 数据
///
/// 依赖 IPlcClient 实现底层通信，可配合 S7CommPlusPlcClient 使用。
///
/// 用法：
///   var serializer = new PlcTagSerializer(s7plusClient);
///   await serializer.ReadAsync(statusObj);
///   await serializer.WriteAsync(cmdObj);
/// </summary>
public class PlcTagSerializer : ITagSerializer
{
    private readonly IPlcClient _plc;

    public PlcTagSerializer(IPlcClient plc)
    {
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
    }

    /// <summary>读取对象 — 自动识别结构体或独立标签模式</summary>
    public async Task ReadAsync(object obj)
    {
        var structAttr = obj.GetType().GetCustomAttribute<PlcStructAttribute>();

        if (structAttr != null)
            await ReadStructAsync(obj, structAttr);
        else
            await ReadTagsAsync(obj);
    }

    /// <summary>写入对象 — 自动识别结构体或独立标签模式</summary>
    public async Task WriteAsync(object obj)
    {
        var structAttr = obj.GetType().GetCustomAttribute<PlcStructAttribute>();

        if (structAttr != null)
            await WriteStructAsync(obj, structAttr);
        else
            await WriteTagsAsync(obj);
    }

    private async Task ReadStructAsync(object obj, PlcStructAttribute attr)
    {
        var props = GetPlcProperties(obj.GetType());
        var names = props.Select(p => $"{attr.Path}.{p.Name}").ToArray();

        var values = await _plc.ReadBatchAsync(names, attr.TimeoutMs);

        for (int i = 0; i < props.Length; i++)
        {
            if (values[i] != null)
                props[i].SetValue(obj, Convert.ChangeType(values[i], props[i].PropertyType));
        }
    }

    private async Task WriteStructAsync(object obj, PlcStructAttribute attr)
    {
        var props = GetPlcProperties(obj.GetType());
        var writes = props
            .Select(p => ($"{attr.Path}.{p.Name}", p.GetValue(obj)))
            .ToList();

        await _plc.WriteBatchAsync(writes, attr.TimeoutMs);
    }

    private async Task ReadTagsAsync(object obj)
    {
        foreach (var prop in GetPlcProperties(obj.GetType()))
        {
            var tag = prop.GetCustomAttribute<PlcTagAttribute>();
            if (tag == null || !tag.Monitored) continue;

            var value = await _plc.ReadAsync(tag.Name);
            if (value != null)
                prop.SetValue(obj, Convert.ChangeType(value, prop.PropertyType));
        }
    }

    private async Task WriteTagsAsync(object obj)
    {
        foreach (var prop in GetPlcProperties(obj.GetType()))
        {
            var tag = prop.GetCustomAttribute<PlcTagAttribute>();
            if (tag == null) continue;

            await _plc.WriteAsync(tag.Name, prop.GetValue(obj));
        }
    }

    /// <summary>过滤出需要映射到 PLC 的属性（忽略带 [PlcIgnore] 的属性）</summary>
    private static PropertyInfo[] GetPlcProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Where(p => p.GetCustomAttribute<PlcIgnoreAttribute>() == null)
            .ToArray();
    }

    /// <summary>检查连接状态</summary>
    public Task<bool> CheckHealthAsync()
    {
        // S7CommPlusPlcClient 内部有连接管理，首次调用会自动连接
        try
        {
            if (_plc is S7CommPlusPlcClient plus)
                return Task.FromResult(plus.IsConnected);
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }
}
