namespace Wcs.Core.StateCenter.Implementation;

using System.Collections.Concurrent;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Features;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 设备状态管理器 — 独立管理设备状态的 ConcurrentDictionary、diff、通知
/// </summary>
public class DeviceStateManager
{
    private readonly ConcurrentDictionary<string, DeviceState> _deviceStates = new();
    private readonly List<IStateChangeListener> _listeners = new();
    private readonly object _listenerLock = new();
    private readonly KeyedEventChannel<DeviceState> _channel = new();
    private readonly IEventBus? _eventBus;

    public DeviceStateManager(IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

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

    public void UpdateDeviceState(string deviceId, DeviceState state)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(state);

        var oldState = _deviceStates.TryGetValue(deviceId, out var old) ? old : null;

        // Diff: 状态未变则不触发通知
        if (oldState != null && oldState.Status == state.Status)
            return;

        _deviceStates.AddOrUpdate(deviceId, state, (_, _) => state);

        if (BatchScope.IsInBatch)
        {
            BatchScope.Current!.AddChange(new StateChangeRecord(
                oldState == null ? StateChangeType.Added : StateChangeType.Updated,
                deviceId, oldState, state));
        }
        else
        {
            NotifyDeviceStateChanged(deviceId, oldState, state);
            _channel.Publish(deviceId, state);
            _eventBus?.PublishAsync(new DeviceStateChangedEvent
            {
                DeviceId = deviceId,
                OldStatus = oldState?.Status ?? DeviceStatusEnum.Offline,
                NewStatus = state.Status,
                DeviceState = state
            });
        }
    }

    public DeviceState? GetDeviceState(string deviceId)
    {
        _deviceStates.TryGetValue(deviceId, out var state);
        return state;
    }

    public IEnumerable<DeviceState> GetAllDeviceStates()
    {
        return _deviceStates.Values.ToList();
    }

    public IDisposable Watch(string deviceId, Action<DeviceState> handler)
        => _channel.Subscribe(deviceId, handler);

    public Dictionary<string, DeviceState> GetSnapshot()
        => new(_deviceStates);

    public void RestoreFromSnapshot(Dictionary<string, DeviceState> snapshot)
    {
        _deviceStates.Clear();
        foreach (var kvp in snapshot)
            _deviceStates.TryAdd(kvp.Key, kvp.Value);
    }

    public void Clear() => _deviceStates.Clear();

    public int Count => _deviceStates.Count;

    private void NotifyDeviceStateChanged(string deviceId, DeviceState? oldState, DeviceState newState)
    {
        List<IStateChangeListener> listeners;
        lock (_listenerLock)
        {
            listeners = new List<IStateChangeListener>(_listeners);
        }

        foreach (var listener in listeners)
        {
            try { listener.OnDeviceStateChanged(deviceId, oldState!, newState); }
            catch { /* Suppress listener exceptions */ }
        }
    }
}
