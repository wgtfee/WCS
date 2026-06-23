using System.Reflection;

namespace Wcs.Core.PlcSubsystem;

public class PlcSerializer
{
    private readonly IPlcClient _plc;

    public PlcSerializer(IPlcClient plc)
    {
        _plc = plc;
    }

    /// <summary>读取对象</summary>
    public async Task ReadAsync(object obj)
    {
        var structAttr = obj.GetType().GetCustomAttribute<PlcStructAttribute>();

        if (structAttr != null)
            await ReadStructAsync(obj, structAttr);
        else
            await ReadTagsAsync(obj);
    }

    /// <summary>写入对象</summary>
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
        var names = props.Select(p => $"{attr.Path}.{p.Name}").ToList();

        var values = await _plc.ReadBatchAsync(names, attr.TimeoutMs);

        for (int i = 0; i < props.Length; i++)
            props[i].SetValue(obj, Convert.ChangeType(values[i], props[i].PropertyType));
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

    /// <summary>过滤出需要映射到 PLC 的属性</summary>
    private static PropertyInfo[] GetPlcProperties(Type type)
    {
        return type.GetProperties()
            .Where(p => p.CanWrite)
            .Where(p => p.GetCustomAttribute<PlcIgnoreAttribute>() == null)
            .ToArray();
    }
}