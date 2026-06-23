using System.Reflection;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.Modbus;

/// <summary>
/// Modbus 标签序列化器 — 使用 [PlcModbusBlock] / [PlcModbusTag] 特性读写寄存器
///
/// 自动构造标签名：{RegisterType}:{Offset}
/// 例如 [PlcModbusBlock("HR")] + [PlcModbusTag(0)] → "HR:0"
/// </summary>
public class ModbusTagSerializer : ITagSerializer
{
    private readonly IPlcClient _plc;

    public ModbusTagSerializer(IPlcClient plc)
    {
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
    }

    /// <summary>读取对象所有 [PlcModbusTag] 属性</summary>
    public async Task ReadAsync(object obj)
    {
        var blockAttr = obj.GetType().GetCustomAttribute<PlcModbusBlockAttribute>();
        if (blockAttr != null)
            await ReadBlockAsync(obj, blockAttr);
        else
            await ReadTagsAsync(obj);
    }

    /// <summary>写入对象所有 [PlcModbusTag] 属性</summary>
    public async Task WriteAsync(object obj)
    {
        var blockAttr = obj.GetType().GetCustomAttribute<PlcModbusBlockAttribute>();
        if (blockAttr != null)
            await WriteBlockAsync(obj, blockAttr);
        else
            await WriteTagsAsync(obj);
    }

    private async Task ReadBlockAsync(object obj, PlcModbusBlockAttribute attr)
    {
        var props = GetModbusProperties(obj.GetType());
        var names = props.Select(p =>
        {
            var tag = p.GetCustomAttribute<PlcModbusTagAttribute>()!;
            return $"{attr.RegisterType}:{tag.Offset}";
        }).ToArray();

        var values = await _plc.ReadBatchAsync(names, attr.TimeoutMs);

        for (int i = 0; i < props.Length; i++)
        {
            if (values[i] != null)
                props[i].SetValue(obj, Convert.ChangeType(values[i], props[i].PropertyType));
        }
    }

    private async Task WriteBlockAsync(object obj, PlcModbusBlockAttribute attr)
    {
        var props = GetModbusProperties(obj.GetType());
        var writes = props.Select(p =>
        {
            var tag = p.GetCustomAttribute<PlcModbusTagAttribute>()!;
            var name = $"{attr.RegisterType}:{tag.Offset}";
            return (name, p.GetValue(obj));
        }).ToList();

        await _plc.WriteBatchAsync(writes, attr.TimeoutMs);
    }

    private async Task ReadTagsAsync(object obj)
    {
        foreach (var prop in GetModbusProperties(obj.GetType()))
        {
            var tag = prop.GetCustomAttribute<PlcModbusTagAttribute>()!;
            var block = prop.DeclaringType?.GetCustomAttribute<PlcModbusBlockAttribute>();
            var name = block != null ? $"{block.RegisterType}:{tag.Offset}" : $"{tag.Offset}";

            var value = await _plc.ReadAsync(name);
            if (value != null)
                prop.SetValue(obj, Convert.ChangeType(value, prop.PropertyType));
        }
    }

    private async Task WriteTagsAsync(object obj)
    {
        foreach (var prop in GetModbusProperties(obj.GetType()))
        {
            var tag = prop.GetCustomAttribute<PlcModbusTagAttribute>()!;
            var block = prop.DeclaringType?.GetCustomAttribute<PlcModbusBlockAttribute>();
            var name = block != null ? $"{block.RegisterType}:{tag.Offset}" : $"{tag.Offset}";

            await _plc.WriteAsync(name, prop.GetValue(obj));
        }
    }

    private static PropertyInfo[] GetModbusProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Where(p => p.GetCustomAttribute<PlcModbusTagAttribute>() != null)
            .Where(p => p.GetCustomAttribute<PlcIgnoreAttribute>() == null)
            .ToArray();
    }

    public Task<bool> CheckHealthAsync()
    {
        try
        {
            if (_plc is ModbusPlcClient modbus)
                return Task.FromResult(modbus.IsConnected);
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }
}
