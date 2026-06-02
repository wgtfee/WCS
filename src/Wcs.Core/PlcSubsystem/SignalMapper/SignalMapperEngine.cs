namespace Wcs.Core.PlcSubsystem.SignalMapper;

using System.Collections.Concurrent;
using System.Reflection;
using Wcs.Core.EventBus.Events;

/// <summary>
/// 信号映射引擎 — 将 PlcBlockDiff 解析为一组业务信号事件
/// 通过 PlcBlockChangePublisher 接收变化通知
/// </summary>
public class SignalMapperEngine : ISignalMapper, IPlcBlockChangeHandler, IDisposable
{
    private readonly ConcurrentDictionary<string, SignalDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, List<SignalDefinition>> _blockIndex = new(); // "PlcName:BlockNumber" → definitions
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// 注册信号映射定义
    /// </summary>
    public void RegisterDefinition(SignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        lock (_lock)
        {
            _definitions[definition.SignalId] = definition;
            RebuildBlockIndex();
        }
    }

    /// <summary>
    /// 批量注册
    /// </summary>
    public void RegisterDefinitions(IEnumerable<SignalDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        lock (_lock)
        {
            foreach (var def in definitions)
                _definitions[def.SignalId] = def;
            RebuildBlockIndex();
        }
    }

    /// <summary>
    /// 移除信号定义
    /// </summary>
    public bool RemoveDefinition(string signalId)
    {
        lock (_lock)
        {
            if (_definitions.TryRemove(signalId, out _))
            {
                RebuildBlockIndex();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 获取所有定义
    /// </summary>
    public IReadOnlyList<SignalDefinition> GetDefinitions()
    {
        return _definitions.Values.ToList();
    }

    /// <summary>
    /// 启用/禁用指定信号
    /// </summary>
    public void SetEnabled(string signalId, bool enabled)
    {
        if (_definitions.TryGetValue(signalId, out var def))
            def.Enabled = enabled;
    }

    public int DefinitionCount => _definitions.Count;

    /// <summary>
    /// 解析 PLC 块差异，生成对应的业务信号事件
    /// </summary>
    public IReadOnlyList<IEvent> Resolve(PlcBlockDiff diff)
    {
        var events = new List<IEvent>();
        var key = GetBlockKey(diff.PlcName, diff.BlockNumber);

        List<SignalDefinition>? candidates;
        lock (_lock)
        {
            _blockIndex.TryGetValue(key, out candidates);
        }

        if (candidates == null || candidates.Count == 0)
            return events;

        var changedOffsets = new HashSet<int>(diff.Changes.Select(c => c.Offset));

        foreach (var def in candidates)
        {
            if (!def.Enabled)
                continue;

            if (!changedOffsets.Contains(def.ByteOffset))
                continue;

            var change = diff.Changes.FirstOrDefault(c => c.Offset == def.ByteOffset);
            if (change == null)
                continue;

            var evt = CreateEvent(def, change, diff.NewData);
            if (evt != null)
                events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// 处理 PlcBlockDiff（IPlcBlockChangeHandler 接口实现）
    /// </summary>
    public Task HandleBlockChangeAsync(PlcBlockDiff diff, CancellationToken cancellationToken = default)
    {
        // 此方法供 PlcBlockChangePublisher 回调
        // SignalMapperEngine 本身不发布事件，调用方需调用 Resolve 并发布
        return Task.CompletedTask;
    }

    /// <summary>
    /// 根据定义和数据创建业务事件实例
    /// </summary>
    private IEvent? CreateEvent(SignalDefinition def, PlcBlockChange change, byte[] newData)
    {
        // 通过反射创建目标事件类型实例
        var eventType = Type.GetType(def.TargetEventType);
        if (eventType == null || !typeof(IEvent).IsAssignableFrom(eventType))
            return null;

        IEvent evt;
        try
        {
            evt = (IEvent)Activator.CreateInstance(eventType)!;
        }
        catch
        {
            return null;
        }

        // 解析值
        object? value = ExtractValue(def, change, newData);

        // 设置事件属性
        if (evt is EventBase eventBase)
        {
            foreach (var mapping in def.PropertyMappings)
            {
                var prop = eventType.GetProperty(mapping.Key,
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    if (mapping.Key == mapping.Value && value != null)
                    {
                        // 直接映射：属性名 = "Value"
                        SetPropertyValue(prop, eventBase, value);
                    }
                    else if (mapping.Value.StartsWith("$"))
                    {
                        // 固定值：$value
                        SetPropertyValue(prop, eventBase, mapping.Value.TrimStart('$'));
                    }
                    else if (mapping.Value.StartsWith("@"))
                    {
                        // 引用其他属性
                        var refProp = eventType.GetProperty(mapping.Value.TrimStart('@'),
                            BindingFlags.Public | BindingFlags.Instance);
                        if (refProp != null)
                        {
                            var refValue = refProp.GetValue(eventBase);
                            SetPropertyValue(prop, eventBase, refValue);
                        }
                    }
                }
            }
        }

        return evt;
    }

    /// <summary>
    /// 提取 PLC 数据值
    /// </summary>
    private static object? ExtractValue(SignalDefinition def, PlcBlockChange change, byte[] newData)
    {
        if (def.DataType.Equals("bool", StringComparison.OrdinalIgnoreCase))
        {
            if (def.BitOffset >= 0 && def.BitOffset <= 7)
                return ((change.NewValue >> def.BitOffset) & 1) == 1;
            return change.NewValue != 0;
        }

        if (def.DataType.Equals("byte", StringComparison.OrdinalIgnoreCase))
            return change.NewValue;

        if (def.DataType.Equals("int", StringComparison.OrdinalIgnoreCase) ||
            def.DataType.Equals("short", StringComparison.OrdinalIgnoreCase))
        {
            if (def.ByteOffset + 2 <= newData.Length)
                return BitConverter.ToInt16(newData, def.ByteOffset);
            return 0;
        }

        if (def.DataType.Equals("word", StringComparison.OrdinalIgnoreCase) ||
            def.DataType.Equals("ushort", StringComparison.OrdinalIgnoreCase))
        {
            if (def.ByteOffset + 2 <= newData.Length)
                return BitConverter.ToUInt16(newData, def.ByteOffset);
            return 0;
        }

        if (def.DataType.Equals("dword", StringComparison.OrdinalIgnoreCase) ||
            def.DataType.Equals("int32", StringComparison.OrdinalIgnoreCase))
        {
            if (def.ByteOffset + 4 <= newData.Length)
                return BitConverter.ToInt32(newData, def.ByteOffset);
            return 0;
        }

        return change.NewValue;
    }

    private static void SetPropertyValue(PropertyInfo prop, object target, object? value)
    {
        try
        {
            if (value == null) return;

            var targetType = prop.PropertyType;
            if (targetType.IsInstanceOfType(value))
            {
                prop.SetValue(target, value);
            }
            else
            {
                var converted = Convert.ChangeType(value, targetType);
                prop.SetValue(target, converted);
            }
        }
        catch
        {
            // 类型转换失败时跳过
        }
    }

    private void RebuildBlockIndex()
    {
        _blockIndex.Clear();
        foreach (var def in _definitions.Values)
        {
            var key = GetBlockKey(def.PlcName, def.BlockNumber);
            var list = _blockIndex.GetOrAdd(key, _ => new List<SignalDefinition>());
            list.Add(def);
        }
    }

    private static string GetBlockKey(string plcName, int blockNumber)
        => $"{plcName}:{blockNumber}";

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _definitions.Clear();
            _blockIndex.Clear();
        }
    }
}
