namespace Wcs.Core.PlcSubsystem.SignalMapper;

using System.Collections.Concurrent;
using System.Reflection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.PlcSubsystem.SignalMapper.Validation;

/// <summary>
/// 信号映射引擎 — 将 PlcBlockDiff 解析为一组业务信号事件
/// 支持：JSON 配置批量加载、信号验证管道（工位级/设备级验证）
/// </summary>
public class SignalMapperEngine : ISignalMapper, IPlcBlockChangeHandler, IDisposable
{
    private readonly ConcurrentDictionary<string, SignalDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, List<SignalDefinition>> _blockIndex = new();
    private readonly List<ISignalValidator> _validators = new();
    private readonly object _lock = new();
    private bool _disposed;

    // ==================== 注册 ====================

    public void RegisterDefinition(SignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_lock)
        {
            _definitions[definition.SignalId] = definition;
            RebuildBlockIndex();
        }
    }

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
    /// 从 JSON 配置批量加载信号定义（推荐方式）
    /// 配置文件结构见 appsettings.json → "Signals" 节
    /// </summary>
    public void LoadFromConfig(IEnumerable<SignalConfigItem> configItems)
    {
        ArgumentNullException.ThrowIfNull(configItems);
        var defs = new List<SignalDefinition>();

        foreach (var item in configItems)
        {
            defs.Add(new SignalDefinition
            {
                SignalId = item.SignalId,
                PlcName = item.PlcName,
                BlockNumber = item.BlockNumber,
                ByteOffset = item.ByteOffset,
                BitOffset = item.BitOffset,
                DataType = item.DataType,
                TargetEventType = item.TargetEventType,
                PropertyMappings = item.PropertyMappings ?? new(),
                Description = item.Description,
                Enabled = item.Enabled
            });
        }

        RegisterDefinitions(defs);
    }

    // ==================== 验证器 ====================

    /// <summary>
    /// 注册信号验证器（工位级/设备级业务验证）
    /// </summary>
    public void RegisterValidator(ISignalValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        lock (_validators)
        {
            _validators.Add(validator);
        }
    }

    /// <summary>
    /// 注册多个验证器
    /// </summary>
    public void RegisterValidators(IEnumerable<ISignalValidator> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        lock (_validators)
        {
            _validators.AddRange(validators);
        }
    }

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

    public IReadOnlyList<SignalDefinition> GetDefinitions()
    {
        return _definitions.Values.ToList();
    }

    public void SetEnabled(string signalId, bool enabled)
    {
        if (_definitions.TryGetValue(signalId, out var def))
            def.Enabled = enabled;
    }

    public int DefinitionCount => _definitions.Count;

    // ==================== 解析（含验证管道） ====================

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
            if (evt == null)
                continue;

            // === 验证管道 ===
            var validationResult = RunValidators(def, diff, events);
            if (validationResult != null)
            {
                switch (validationResult.Action)
                {
                    case SignalValidationAction.Reject:
                        continue; // 跳过此信号
                    case SignalValidationAction.Defer:
                        // 延迟信号暂不处理（可放入重试队列）
                        continue;
                    case SignalValidationAction.Pass:
                        break; // 通过，正常发布
                }
            }

            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// 运行所有匹配的验证器
    /// </summary>
    private SignalValidationResult? RunValidators(
        SignalDefinition def, PlcBlockDiff diff, List<IEvent> generatedEvents)
    {
        List<ISignalValidator> snapshot;
        lock (_validators)
        {
            snapshot = _validators.ToList();
        }

        foreach (var validator in snapshot)
        {
            // 验证器按 DeviceId + SignalId 过滤
            if (validator.DeviceId != null)
            {
                var signalDeviceId = def.PropertyMappings.GetValueOrDefault("DeviceId");
                if (signalDeviceId != validator.DeviceId)
                    continue;
            }
            if (validator.SignalId != null && validator.SignalId != def.SignalId)
                continue;

            var result = validator.Validate(def, diff, generatedEvents);
            if (result != null && result.Action != SignalValidationAction.Pass)
                return result; // 有验证器拒绝或延迟
        }

        return null; // 全部通过
    }

    // ==================== IPlcBlockChangeHandler ====================

    public Task HandleBlockChangeAsync(PlcBlockDiff diff, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    // ==================== 事件创建 ====================

    private IEvent? CreateEvent(SignalDefinition def, PlcBlockChange change, byte[] newData)
    {
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

        var value = ExtractValue(def, change, newData);

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
                        SetPropertyValue(prop, eventBase, value);
                    }
                    else if (mapping.Value.StartsWith("$"))
                    {
                        SetPropertyValue(prop, eventBase, mapping.Value.TrimStart('$'));
                    }
                    else if (mapping.Value.StartsWith("@"))
                    {
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
                prop.SetValue(target, value);
            else
            {
                var converted = Convert.ChangeType(value, targetType);
                prop.SetValue(target, converted);
            }
        }
        catch { }
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

/// <summary>
/// JSON 配置中单个信号定义的扁平模型（专用于反序列化）
/// </summary>
public class SignalConfigItem
{
    public string SignalId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public int BlockNumber { get; set; }
    public int ByteOffset { get; set; }
    public int BitOffset { get; set; } = -1;
    public string DataType { get; set; } = "bool";
    public string TargetEventType { get; set; } = string.Empty;
    public Dictionary<string, string>? PropertyMappings { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
}
