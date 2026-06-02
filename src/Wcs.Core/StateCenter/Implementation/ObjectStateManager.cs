namespace Wcs.Core.StateCenter.Implementation;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Features;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 物体/物料状态管理器
/// </summary>
public class ObjectStateManager
{
    private readonly ConcurrentDictionary<string, ObjectState> _objectStates = new();
    private readonly KeyedEventChannel<ObjectState> _channel = new();

    public void UpdateObjectState(string objectId, ObjectState state)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(state);

        _objectStates.AddOrUpdate(objectId, state, (_, _) => state);

        if (!BatchScope.IsInBatch)
        {
            _channel.Publish(objectId, state);
        }
    }

    public ObjectState? GetObjectState(string objectId)
    {
        _objectStates.TryGetValue(objectId, out var state);
        return state;
    }

    public IEnumerable<ObjectState> GetAllObjectStates()
        => _objectStates.Values.ToList();

    public IDisposable Watch(string objectId, Action<ObjectState> handler)
        => _channel.Subscribe(objectId, handler);

    public Dictionary<string, ObjectState> GetSnapshot()
        => new(_objectStates);

    public void RestoreFromSnapshot(Dictionary<string, ObjectState> snapshot)
    {
        _objectStates.Clear();
        foreach (var kvp in snapshot)
            _objectStates.TryAdd(kvp.Key, kvp.Value);
    }

    public void Clear() => _objectStates.Clear();

    public int Count => _objectStates.Count;
}
