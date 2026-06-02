namespace Wcs.Core.StateCenter.Implementation;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Features;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 报警状态管理器
/// </summary>
public class AlarmStateManager
{
    private readonly ConcurrentDictionary<string, AlarmState> _alarmStates = new();
    private readonly List<IStateChangeListener> _listeners = new();
    private readonly object _listenerLock = new();
    private readonly KeyedEventChannel<AlarmState> _channel = new();

    public void RegisterListener(IStateChangeListener listener)
    {
        lock (_listenerLock)
        {
            if (!_listeners.Contains(listener))
                _listeners.Add(listener);
        }
    }

    public void UnregisterListener(IStateChangeListener listener)
    {
        lock (_listenerLock)
        {
            _listeners.Remove(listener);
        }
    }

    public void UpdateAlarmState(string alarmId, AlarmState state)
    {
        ArgumentNullException.ThrowIfNull(alarmId);
        ArgumentNullException.ThrowIfNull(state);

        var oldState = _alarmStates.TryGetValue(alarmId, out var old) ? old : null;

        if (oldState != null && oldState.Status == state.Status && oldState.Level == state.Level)
            return;

        _alarmStates.AddOrUpdate(alarmId, state, (_, _) => state);

        if (BatchScope.IsInBatch)
        {
            BatchScope.Current!.AddChange(new StateChangeRecord(
                oldState == null ? StateChangeType.Added : StateChangeType.Updated,
                alarmId, oldState, state));
        }
        else
        {
            NotifyAlarmStateChanged(alarmId, oldState, state);
            _channel.Publish(alarmId, state);
        }
    }

    public AlarmState? GetAlarmState(string alarmId)
    {
        _alarmStates.TryGetValue(alarmId, out var state);
        return state;
    }

    public IEnumerable<AlarmState> GetActiveAlarms()
    {
        return _alarmStates.Values
            .Where(a => a.Status == AlarmStatusEnum.Active || a.Status == AlarmStatusEnum.Acknowledged)
            .ToList();
    }

    public IDisposable Watch(string alarmId, Action<AlarmState> handler)
        => _channel.Subscribe(alarmId, handler);

    public Dictionary<string, AlarmState> GetSnapshot()
        => new(_alarmStates);

    public void RestoreFromSnapshot(Dictionary<string, AlarmState> snapshot)
    {
        _alarmStates.Clear();
        foreach (var kvp in snapshot)
            _alarmStates.TryAdd(kvp.Key, kvp.Value);
    }

    public void Clear() => _alarmStates.Clear();

    public int Count => _alarmStates.Count;

    private void NotifyAlarmStateChanged(string alarmId, AlarmState? oldState, AlarmState newState)
    {
        List<IStateChangeListener> listeners;
        lock (_listenerLock)
        {
            listeners = new List<IStateChangeListener>(_listeners);
        }

        foreach (var listener in listeners)
        {
            try { listener.OnAlarmStateChanged(alarmId, oldState!, newState); }
            catch { }
        }
    }
}
