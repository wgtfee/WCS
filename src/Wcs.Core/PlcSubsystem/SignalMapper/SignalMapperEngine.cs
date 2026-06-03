namespace Wcs.Core.PlcSubsystem.SignalMapper;

using System.Collections.Concurrent;
using System.Reflection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.PlcSubsystem.SignalMapper.Validation;
using Wcs.Core.StateCenter.Interfaces;

public class SignalMapperEngine : ISignalMapper, IPlcBlockChangeHandler, IDisposable
{
    private readonly ConcurrentDictionary<string, SignalDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, List<SignalDefinition>> _blockIndex = new();
    private readonly List<ISignalValidator> _validators = new();
    private readonly IStateCenter _stateCenter;
    private readonly object _lock = new();
    private bool _disposed;

    public SignalMapperEngine(IStateCenter stateCenter)
    {
        _stateCenter = stateCenter ?? throw new ArgumentNullException(nameof(stateCenter));
    }

    public void RegisterDefinition(SignalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_lock) { _definitions[definition.SignalId] = definition; RebuildBlockIndex(); }
    }

    public void RegisterDefinitions(IEnumerable<SignalDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        lock (_lock)
        {
            foreach (var def in definitions) _definitions[def.SignalId] = def;
            RebuildBlockIndex();
        }
    }

    public void LoadFromConfig(IEnumerable<SignalConfigItem> configItems)
    {
        var defs = new List<SignalDefinition>();
        foreach (var item in configItems)
        {
            defs.Add(new SignalDefinition
            {
                SignalId = item.SignalId, PlcName = item.PlcName,
                BlockNumber = item.BlockNumber, ByteOffset = item.ByteOffset,
                BitOffset = item.BitOffset, DataType = item.DataType,
                TargetEventType = item.TargetEventType,
                PropertyMappings = item.PropertyMappings ?? new(),
                Description = item.Description, Enabled = item.Enabled
            });
        }
        RegisterDefinitions(defs);
    }

    public void RegisterValidator(ISignalValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        lock (_validators) { _validators.Add(validator); }
    }

    public void RegisterValidators(IEnumerable<ISignalValidator> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        lock (_validators) { _validators.AddRange(validators); }
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

    public IReadOnlyList<SignalDefinition> GetDefinitions() => _definitions.Values.ToList();
    public void SetEnabled(string signalId, bool enabled) { if (_definitions.TryGetValue(signalId, out var d)) d.Enabled = enabled; }
    public int DefinitionCount => _definitions.Count;

    public IReadOnlyList<IEvent> Resolve(PlcBlockDiff diff)
    {
        var events = new List<IEvent>();
        var key = $"{diff.PlcName}:{diff.BlockNumber}";
        List<SignalDefinition>? candidates;
        lock (_lock) { _blockIndex.TryGetValue(key, out candidates); }
        if (candidates == null) return events;

        var changed = new HashSet<int>(diff.Changes.Select(c => c.Offset));
        foreach (var def in candidates)
        {
            if (!def.Enabled) continue;
            if (!changed.Contains(def.ByteOffset)) continue;
            var change = diff.Changes.FirstOrDefault(c => c.Offset == def.ByteOffset);
            if (change == null) continue;

            var evt = CreateEvent(def, change, diff.NewData);
            if (evt == null) continue;

            var ctx = new ValidatorContext(_stateCenter, def, diff, events);
            var result = RunValidators(ctx);
            if (result != null && result.Action != SignalValidationAction.Pass) continue;
            events.Add(evt);
        }
        return events;
    }

    private SignalValidationResult? RunValidators(ValidatorContext ctx)
    {
        List<ISignalValidator> snapshot;
        lock (_validators) { snapshot = _validators.ToList(); }
        foreach (var v in snapshot)
        {
            if (v.DeviceId != null)
            {
                var devId = ctx.Definition.PropertyMappings.GetValueOrDefault("DeviceId");
                if (devId != v.DeviceId) continue;
            }
            if (v.SignalId != null && v.SignalId != ctx.Definition.SignalId) continue;
            var result = v.Validate(ctx);
            if (result != null && result.Action != SignalValidationAction.Pass) return result;
        }
        return null;
    }

    public Task HandleBlockChangeAsync(PlcBlockDiff diff, CancellationToken ct = default) => Task.CompletedTask;

    private IEvent? CreateEvent(SignalDefinition def, PlcBlockChange change, byte[] newData)
    {
        var t = Type.GetType(def.TargetEventType);
        if (t == null || !typeof(IEvent).IsAssignableFrom(t)) return null;
        IEvent evt;
        try { evt = (IEvent)Activator.CreateInstance(t)!; } catch { return null; }
        var val = ExtractValue(def, change, newData);
        if (evt is EventBase eb)
        {
            foreach (var m in def.PropertyMappings)
            {
                var p = t.GetProperty(m.Key, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) continue;
                if (m.Key == m.Value && val != null) SetProp(p, eb, val);
                else if (m.Value.StartsWith("$")) SetProp(p, eb, m.Value.TrimStart('$'));
                else if (m.Value.StartsWith("@"))
                {
                    var rp = t.GetProperty(m.Value.TrimStart('@'), BindingFlags.Public | BindingFlags.Instance);
                    if (rp != null) SetProp(p, eb, rp.GetValue(eb));
                }
            }
        }
        return evt;
    }

    private static object? ExtractValue(SignalDefinition def, PlcBlockChange c, byte[] data)
    {
        if (def.DataType.Equals("bool", StringComparison.OrdinalIgnoreCase))
            return def.BitOffset >= 0 ? ((c.NewValue >> def.BitOffset) & 1) == 1 : c.NewValue != 0;
        if (def.DataType.Equals("byte", StringComparison.OrdinalIgnoreCase)) return c.NewValue;
        if ((def.DataType == "int" || def.DataType == "short") && def.ByteOffset + 2 <= data.Length)
            return BitConverter.ToInt16(data, def.ByteOffset);
        if ((def.DataType == "word" || def.DataType == "ushort") && def.ByteOffset + 2 <= data.Length)
            return BitConverter.ToUInt16(data, def.ByteOffset);
        if ((def.DataType == "dword" || def.DataType == "int32") && def.ByteOffset + 4 <= data.Length)
            return BitConverter.ToInt32(data, def.ByteOffset);
        return c.NewValue;
    }

    private static void SetProp(PropertyInfo p, object t, object? v)
    {
        try { if (v != null) p.SetValue(t, p.PropertyType.IsInstanceOfType(v) ? v : Convert.ChangeType(v, p.PropertyType)); }
        catch { }
    }

    private void RebuildBlockIndex()
    {
        _blockIndex.Clear();
        foreach (var def in _definitions.Values)
        {
            var key = $"{def.PlcName}:{def.BlockNumber}";
            var list = _blockIndex.GetOrAdd(key, _ => new List<SignalDefinition>());
            list.Add(def);
        }
    }

    public void Dispose()
    {
        if (!_disposed) { _disposed = true; _definitions.Clear(); _blockIndex.Clear(); }
    }
}

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
