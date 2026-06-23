using System.Reflection;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.OpcUa;

/// <summary>
/// OPC UA 标签序列化器 — 使用 [PlcOpcUaBlock] / [PlcOpcUaTag] 特性读写节点
///
/// 标签名直接使用 NodeId 字符串，例如 "ns=2;s=CV01.Speed"
/// </summary>
public class OpcUaTagSerializer : ITagSerializer
{
    private readonly IPlcClient _plc;

    public OpcUaTagSerializer(IPlcClient plc)
    {
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
    }

    /// <summary>读取对象所有 [PlcOpcUaTag] 属性</summary>
    public async Task ReadAsync(object obj)
    {
        var blockAttr = obj.GetType().GetCustomAttribute<PlcOpcUaBlockAttribute>();
        if (blockAttr != null)
            await ReadBlockAsync(obj, blockAttr);
        else
            await ReadTagsAsync(obj);
    }

    /// <summary>写入对象所有 [PlcOpcUaTag] 属性</summary>
    public async Task WriteAsync(object obj)
    {
        var blockAttr = obj.GetType().GetCustomAttribute<PlcOpcUaBlockAttribute>();
        if (blockAttr != null)
            await WriteBlockAsync(obj, blockAttr);
        else
            await WriteTagsAsync(obj);
    }

    private async Task ReadBlockAsync(object obj, PlcOpcUaBlockAttribute attr)
    {
        var props = GetOpcUaProperties(obj.GetType());
        var names = props.Select(p =>
            p.GetCustomAttribute<PlcOpcUaTagAttribute>()!.NodeId
        ).ToArray();

        var values = await _plc.ReadBatchAsync(names, attr.TimeoutMs);

        for (int i = 0; i < props.Length; i++)
        {
            if (values[i] != null)
                props[i].SetValue(obj, Convert.ChangeType(values[i], props[i].PropertyType));
        }
    }

    private async Task WriteBlockAsync(object obj, PlcOpcUaBlockAttribute attr)
    {
        var props = GetOpcUaProperties(obj.GetType());
        var writes = props.Select(p =>
        {
            var tag = p.GetCustomAttribute<PlcOpcUaTagAttribute>()!;
            return (tag.NodeId, p.GetValue(obj));
        }).ToList();

        await _plc.WriteBatchAsync(writes, attr.TimeoutMs);
    }

    private async Task ReadTagsAsync(object obj)
    {
        foreach (var prop in GetOpcUaProperties(obj.GetType()))
        {
            var tag = prop.GetCustomAttribute<PlcOpcUaTagAttribute>()!;
            var value = await _plc.ReadAsync(tag.NodeId);
            if (value != null)
                prop.SetValue(obj, Convert.ChangeType(value, prop.PropertyType));
        }
    }

    private async Task WriteTagsAsync(object obj)
    {
        foreach (var prop in GetOpcUaProperties(obj.GetType()))
        {
            var tag = prop.GetCustomAttribute<PlcOpcUaTagAttribute>()!;
            await _plc.WriteAsync(tag.NodeId, prop.GetValue(obj));
        }
    }

    private static PropertyInfo[] GetOpcUaProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Where(p => p.GetCustomAttribute<PlcOpcUaTagAttribute>() != null)
            .Where(p => p.GetCustomAttribute<PlcIgnoreAttribute>() == null)
            .ToArray();
    }

    public Task<bool> CheckHealthAsync()
    {
        try
        {
            if (_plc is OpcUaPlcClient opcua)
                return Task.FromResult(opcua.IsConnected);
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }
}
