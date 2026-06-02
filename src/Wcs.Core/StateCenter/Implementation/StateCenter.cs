namespace Wcs.Core.StateCenter.Implementation;

using System.Collections.Concurrent;
using System.Text.Json;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Features;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// StateCenter 实现 — 基于 ConcurrentDictionary 的系统实时状态中心
/// 增强特性：diff 通知抑制、BatchScope 批量更新、per-key 订阅、可选 EventBus 发布
/// </summary>
public class StateCenter : IStateCenter, ISnapshotProvider
{
    private readonly ConcurrentDictionary<string, DeviceState> _deviceStates = new();
    private readonly ConcurrentDictionary<string, TaskRuntime> _taskRuntimes = new();
    private readonly ConcurrentDictionary<string, AlarmState> _alarmStates = new();
    private readonly ConcurrentDictionary<string, ObjectState> _objectStates = new();
    private readonly ConcurrentDictionary<string, PlcBlockState> _plcBlockStates = new();

    // Legacy listener pattern (backward compat)
    private readonly List<IStateChangeListener> _listeners = new();
    private readonly object _listenerLock = new();

    // Per-key event channels
    private readonly KeyedEventChannel<DeviceState> _deviceChannel = new();
    private readonly KeyedEventChannel<TaskRuntime> _taskChannel = new();
    private readonly KeyedEventChannel<AlarmState> _alarmChannel = new();
    private readonly KeyedEventChannel<ObjectState> _objectChannel = new();

    // Optional EventBus for cross-module integration
    private readonly IEventBus? _eventBus;

    public StateCenter(IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    // ==================== 注册监听器 (legacy) ====================

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

    // ==================== 设备状态 ====================

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
            _deviceChannel.Publish(deviceId, state);
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

    // ==================== 任务运行时 ====================

    public void UpdateTaskRuntime(string taskId, TaskRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(runtime);

        var oldRuntime = _taskRuntimes.TryGetValue(taskId, out var old) ? old : null;

        // Diff: 状态未变则不触发通知
        if (oldRuntime != null && oldRuntime.Status == runtime.Status)
            return;

        _taskRuntimes.AddOrUpdate(taskId, runtime, (_, _) => runtime);

        if (BatchScope.IsInBatch)
        {
            BatchScope.Current!.AddChange(new StateChangeRecord(
                oldRuntime == null ? StateChangeType.Added : StateChangeType.Updated,
                taskId, oldRuntime, runtime));
        }
        else
        {
            NotifyTaskStateChanged(taskId, oldRuntime, runtime);
            _taskChannel.Publish(taskId, runtime);
            _eventBus?.PublishAsync(new TaskStateChangedEvent
            {
                TaskId = taskId,
                OldStatus = oldRuntime?.Status ?? TaskStatusEnum.Created,
                NewStatus = runtime.Status,
                TaskRuntime = runtime
            });
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

    // ==================== 报警状态 ====================

    public void UpdateAlarmState(string alarmId, AlarmState state)
    {
        ArgumentNullException.ThrowIfNull(alarmId);
        ArgumentNullException.ThrowIfNull(state);

        var oldState = _alarmStates.TryGetValue(alarmId, out var old) ? old : null;

        // Diff: 状态未变则不触发通知
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
            _alarmChannel.Publish(alarmId, state);
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

    // ==================== 物体状态 ====================

    public void UpdateObjectState(string objectId, ObjectState state)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(state);

        _objectStates.AddOrUpdate(objectId, state, (_, _) => state);

        if (!BatchScope.IsInBatch)
        {
            _objectChannel.Publish(objectId, state);
        }
    }

    public ObjectState? GetObjectState(string objectId)
    {
        _objectStates.TryGetValue(objectId, out var state);
        return state;
    }

    // ==================== PLC 数据块 ====================

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

    // ==================== 快照 ====================

    public IReadOnlyDictionary<string, T> GetSnapshot<T>()
    {
        var dict = GetDictionary(typeof(T));
        if (dict == null)
            throw new InvalidOperationException($"Unsupported snapshot type: {typeof(T).Name}");

        // ConcurrentDictionary 的枚举器是点时间快照，此处已保证原子一致性
        // 通过 object 中转转换 — 因为 switch 已用 typeof(T) 校验类型，运行时安全
        return dict switch
        {
            ConcurrentDictionary<string, DeviceState> d =>
                (IReadOnlyDictionary<string, T>)(object)new Dictionary<string, DeviceState>(d),
            ConcurrentDictionary<string, TaskRuntime> d =>
                (IReadOnlyDictionary<string, T>)(object)new Dictionary<string, TaskRuntime>(d),
            ConcurrentDictionary<string, AlarmState> d =>
                (IReadOnlyDictionary<string, T>)(object)new Dictionary<string, AlarmState>(d),
            ConcurrentDictionary<string, ObjectState> d =>
                (IReadOnlyDictionary<string, T>)(object)new Dictionary<string, ObjectState>(d),
            ConcurrentDictionary<string, PlcBlockState> d =>
                (IReadOnlyDictionary<string, T>)(object)new Dictionary<string, PlcBlockState>(d),
            _ => throw new InvalidOperationException($"Unsupported snapshot type: {typeof(T).Name}")
        };
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
            _deviceStates.TryAdd(kvp.Key, kvp.Value);

        foreach (var kvp in snapshot.TaskRuntimes)
            _taskRuntimes.TryAdd(kvp.Key, kvp.Value);

        foreach (var kvp in snapshot.AlarmStates)
            _alarmStates.TryAdd(kvp.Key, kvp.Value);

        foreach (var kvp in snapshot.ObjectStates)
            _objectStates.TryAdd(kvp.Key, kvp.Value);

        foreach (var kvp in snapshot.PlcBlockStates)
            _plcBlockStates.TryAdd(kvp.Key, kvp.Value);
    }

    public void Clear()
    {
        _deviceStates.Clear();
        _taskRuntimes.Clear();
        _alarmStates.Clear();
        _objectStates.Clear();
        _plcBlockStates.Clear();
    }

    // ==================== 批量更新 ====================

    public IBatchScope BeginBatch()
    {
        return BatchScope.Begin();
    }

    // ==================== Per-key 订阅 ====================

    public IDisposable WatchDevice(string deviceId, Action<DeviceState> handler)
        => _deviceChannel.Subscribe(deviceId, handler);

    public IDisposable WatchTask(string taskId, Action<TaskRuntime> handler)
        => _taskChannel.Subscribe(taskId, handler);

    public IDisposable WatchAlarm(string alarmId, Action<AlarmState> handler)
        => _alarmChannel.Subscribe(alarmId, handler);

    public IDisposable WatchObject(string objectId, Action<ObjectState> handler)
        => _objectChannel.Subscribe(objectId, handler);

    // ==================== ISnapshotProvider ====================

    string ISnapshotProvider.ModuleName => "StateCenter";
    int ISnapshotProvider.RestoreOrder => 0;

    async Task<object> ISnapshotProvider.CaptureSnapshotAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        return GetSnapshot();
    }

    async Task ISnapshotProvider.RestoreSnapshotAsync(object snapshot, CancellationToken ct)
    {
        await Task.CompletedTask;
        if (snapshot is JsonElement element)
        {
            var stateSnapshot = JsonSerializer.Deserialize<StateSnapshot>(element.GetRawText());
            if (stateSnapshot != null) RestoreFromSnapshot(stateSnapshot);
        }
        else if (snapshot is StateSnapshot stateSnapshot)
        {
            RestoreFromSnapshot(stateSnapshot);
        }
    }

    // ==================== 内部方法 ====================

    private object? GetDictionary(Type type)
    {
        if (type == typeof(DeviceState)) return _deviceStates;
        if (type == typeof(TaskRuntime)) return _taskRuntimes;
        if (type == typeof(AlarmState)) return _alarmStates;
        if (type == typeof(ObjectState)) return _objectStates;
        if (type == typeof(PlcBlockState)) return _plcBlockStates;
        return null;
    }

    private void NotifyDeviceStateChanged(string deviceId, DeviceState? oldState, DeviceState newState)
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
                listener.OnDeviceStateChanged(deviceId, oldState!, newState);
            }
            catch
            {
                // Suppress listener exceptions
            }
        }
    }

    private void NotifyTaskStateChanged(string taskId, TaskRuntime? oldRuntime, TaskRuntime newRuntime)
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
                listener.OnTaskStateChanged(taskId, oldRuntime!, newRuntime);
            }
            catch { }
        }
    }

    private void NotifyAlarmStateChanged(string alarmId, AlarmState? oldState, AlarmState newState)
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
                listener.OnAlarmStateChanged(alarmId, oldState!, newState);
            }
            catch { }
        }
    }
}
