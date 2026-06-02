namespace Wcs.Core.StateCenter.Implementation;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// StateCenter 实现 - 基于 ConcurrentDictionary 的实时状态中心
/// </summary>
public class StateCenter : IStateCenter
{
    private readonly ConcurrentDictionary<string, DeviceState> _deviceStates = new();
    private readonly ConcurrentDictionary<string, TaskRuntime> _taskRuntimes = new();
    private readonly ConcurrentDictionary<string, AlarmState> _alarmStates = new();
    private readonly ConcurrentDictionary<string, ObjectState> _objectStates = new();
    private readonly ConcurrentDictionary<string, PlcBlockState> _plcBlockStates = new();

    private readonly List<IStateChangeListener> _listeners = new();
    private readonly object _listenerLock = new();

    /// <summary>
    /// 注册状态变化监听器
    /// </summary>
    public void RegisterListener(IStateChangeListener listener)
    {
        lock (_listenerLock)
        {
            if (!_listeners.Contains(listener))
            {
                _listeners.Add(listener);
            }
        }
    }

    /// <summary>
    /// 注销状态变化监听器
    /// </summary>
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
        _deviceStates.AddOrUpdate(deviceId, state, (_, _) => state);

        if (oldState != null)
        {
            NotifyDeviceStateChanged(deviceId, oldState, state);
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

    public void UpdateTaskRuntime(string taskId, TaskRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(runtime);

        var oldRuntime = _taskRuntimes.TryGetValue(taskId, out var old) ? old : null;
        _taskRuntimes.AddOrUpdate(taskId, runtime, (_, _) => runtime);

        if (oldRuntime != null)
        {
            NotifyTaskStateChanged(taskId, oldRuntime, runtime);
        }
    }

    public TaskRuntime? GetTaskRuntime(string taskId)
    {
        _taskRuntimes.TryGetValue(taskId, out var runtime);
        return runtime;
    }

    public IEnumerable<TaskRuntime> GetAllActiveTasks()
    {
        return _taskRuntimes.Values
            .Where(t => t.Status != TaskStatusEnum.Completed && t.Status != TaskStatusEnum.Failed)
            .ToList();
    }

    public void UpdateAlarmState(string alarmId, AlarmState state)
    {
        ArgumentNullException.ThrowIfNull(alarmId);
        ArgumentNullException.ThrowIfNull(state);

        var oldState = _alarmStates.TryGetValue(alarmId, out var old) ? old : null;
        _alarmStates.AddOrUpdate(alarmId, state, (_, _) => state);

        if (oldState != null)
        {
            NotifyAlarmStateChanged(alarmId, oldState, state);
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

    public void UpdateObjectState(string objectId, ObjectState state)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(state);

        _objectStates.AddOrUpdate(objectId, state, (_, _) => state);
    }

    public ObjectState? GetObjectState(string objectId)
    {
        _objectStates.TryGetValue(objectId, out var state);
        return state;
    }

    public void UpdatePlcBlockState(string blockName, PlcBlockState state)
    {
        ArgumentNullException.ThrowIfNull(blockName);
        ArgumentNullException.ThrowIfNull(state);

        _plcBlockStates.AddOrUpdate(blockName, state, (_, _) => state);
    }

    public PlcBlockState? GetPlcBlockState(string blockName)
    {
        _plcBlockStates.TryGetValue(blockName, out var state);
        return state;
    }

    public void Clear()
    {
        _deviceStates.Clear();
        _taskRuntimes.Clear();
        _alarmStates.Clear();
        _objectStates.Clear();
        _plcBlockStates.Clear();
    }

    public StateSnapshot GetSnapshot()
    {
        return new StateSnapshot
        {
            SnapshotTime = DateTime.UtcNow,
            DeviceStates = new Dictionary<string, DeviceState>(_deviceStates),
            TaskRuntimes = new Dictionary<string, TaskRuntime>(_taskRuntimes),
            AlarmStates = new Dictionary<string, AlarmState>(_alarmStates),
            ObjectStates = new Dictionary<string, ObjectState>(_objectStates),
            PlcBlockStates = new Dictionary<string, PlcBlockState>(_plcBlockStates)
        };
    }

    public void RestoreFromSnapshot(StateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Clear();

        foreach (var kvp in snapshot.DeviceStates)
        {
            _deviceStates.TryAdd(kvp.Key, kvp.Value);
        }

        foreach (var kvp in snapshot.TaskRuntimes)
        {
            _taskRuntimes.TryAdd(kvp.Key, kvp.Value);
        }

        foreach (var kvp in snapshot.AlarmStates)
        {
            _alarmStates.TryAdd(kvp.Key, kvp.Value);
        }

        foreach (var kvp in snapshot.ObjectStates)
        {
            _objectStates.TryAdd(kvp.Key, kvp.Value);
        }

        foreach (var kvp in snapshot.PlcBlockStates)
        {
            _plcBlockStates.TryAdd(kvp.Key, kvp.Value);
        }
    }

    private void NotifyDeviceStateChanged(string deviceId, DeviceState oldState, DeviceState newState)
    {
        List<IStateChangeListener> listeners;
        lock (_listenerLock)
        {
            listeners = new List<IStateChangeListener>(_listeners);
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener.OnDeviceStateChanged(deviceId, oldState, newState);
            }
            catch
            {
                // Suppress listener exceptions
            }
        }
    }

    private void NotifyTaskStateChanged(string taskId, TaskRuntime oldRuntime, TaskRuntime newRuntime)
    {
        List<IStateChangeListener> listeners;
        lock (_listenerLock)
        {
            listeners = new List<IStateChangeListener>(_listeners);
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener.OnTaskStateChanged(taskId, oldRuntime, newRuntime);
            }
            catch
            {
                // Suppress listener exceptions
            }
        }
    }

    private void NotifyAlarmStateChanged(string alarmId, AlarmState oldState, AlarmState newState)
    {
        List<IStateChangeListener> listeners;
        lock (_listenerLock)
        {
            listeners = new List<IStateChangeListener>(_listeners);
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener.OnAlarmStateChanged(alarmId, oldState, newState);
            }
            catch
            {
                // Suppress listener exceptions
            }
        }
    }
}
