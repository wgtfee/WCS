namespace Wcs.Core.StateCenter.Implementation;

using System.Text.Json;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Features;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// StateCenter 实现 — 系统实时状态中心门面
/// 内部由 5 个独立 Manager 分别管理各类状态，避免单一中心瓶颈
/// </summary>
public class StateCenter : IStateCenter, ISnapshotProvider
{
    /// <summary>设备状态管理器</summary>
    public DeviceStateManager DeviceStateManager { get; }

    /// <summary>任务运行时状态管理器</summary>
    public TaskStateManager TaskStateManager { get; }

    /// <summary>报警状态管理器</summary>
    public AlarmStateManager AlarmStateManager { get; }

    /// <summary>物体状态管理器</summary>
    public ObjectStateManager ObjectStateManager { get; }

    /// <summary>PLC 数据块状态管理器</summary>
    public PlcBlockStateManager PlcBlockStateManager { get; }

    public StateCenter(IEventBus? eventBus = null)
    {
        DeviceStateManager = new DeviceStateManager(eventBus);
        TaskStateManager = new TaskStateManager(eventBus);
        AlarmStateManager = new AlarmStateManager();
        ObjectStateManager = new ObjectStateManager();
        PlcBlockStateManager = new PlcBlockStateManager();
    }

    // ==================== 监听器（委托给 DeviceStateManager） ====================

    public void RegisterListener(IStateChangeListener listener)
    {
        DeviceStateManager.RegisterListener(listener);
        TaskStateManager.RegisterListener(listener);
        AlarmStateManager.RegisterListener(listener);
    }

    public void UnregisterListener(IStateChangeListener listener)
    {
        DeviceStateManager.UnregisterListener(listener);
        TaskStateManager.UnregisterListener(listener);
        AlarmStateManager.UnregisterListener(listener);
    }

    // ==================== 设备状态 ====================

    public void UpdateDeviceState(string deviceId, DeviceState state)
        => DeviceStateManager.UpdateDeviceState(deviceId, state);

    public DeviceState? GetDeviceState(string deviceId)
        => DeviceStateManager.GetDeviceState(deviceId);

    public IEnumerable<DeviceState> GetAllDeviceStates()
        => DeviceStateManager.GetAllDeviceStates();

    // ==================== 任务运行时 ====================

    public void UpdateTaskRuntime(string taskId, TaskRuntime runtime)
        => TaskStateManager.UpdateTaskRuntime(taskId, runtime);

    public TaskRuntime? GetTaskRuntime(string taskId)
        => TaskStateManager.GetTaskRuntime(taskId);

    public IEnumerable<TaskRuntime> GetAllActiveTasks()
        => TaskStateManager.GetAllActiveTasks();

    // ==================== 报警状态 ====================

    public void UpdateAlarmState(string alarmId, AlarmState state)
        => AlarmStateManager.UpdateAlarmState(alarmId, state);

    public AlarmState? GetAlarmState(string alarmId)
        => AlarmStateManager.GetAlarmState(alarmId);

    public IEnumerable<AlarmState> GetActiveAlarms()
        => AlarmStateManager.GetActiveAlarms();

    // ==================== 物体状态 ====================

    public void UpdateObjectState(string objectId, ObjectState state)
        => ObjectStateManager.UpdateObjectState(objectId, state);

    public ObjectState? GetObjectState(string objectId)
        => ObjectStateManager.GetObjectState(objectId);

    // ==================== PLC 数据块 ====================

    public void UpdatePlcBlockState(string blockName, PlcBlockState state)
        => PlcBlockStateManager.UpdatePlcBlockState(blockName, state);

    public PlcBlockState? GetPlcBlockState(string blockName)
        => PlcBlockStateManager.GetPlcBlockState(blockName);

    // ==================== 快照 ====================

    public IReadOnlyDictionary<string, T> GetSnapshot<T>()
    {
        var type = typeof(T);
        if (type == typeof(DeviceState))
            return (IReadOnlyDictionary<string, T>)DeviceStateManager.GetSnapshot();
        if (type == typeof(TaskRuntime))
            return (IReadOnlyDictionary<string, T>)TaskStateManager.GetSnapshot();
        if (type == typeof(AlarmState))
            return (IReadOnlyDictionary<string, T>)AlarmStateManager.GetSnapshot();
        if (type == typeof(ObjectState))
            return (IReadOnlyDictionary<string, T>)ObjectStateManager.GetSnapshot();
        if (type == typeof(PlcBlockState))
            return (IReadOnlyDictionary<string, T>)PlcBlockStateManager.GetSnapshot();
        throw new InvalidOperationException($"Unsupported snapshot type: {typeof(T).Name}");
    }

    public StateSnapshot GetSnapshot()
    {
        return new StateSnapshot
        {
            SnapshotTime = DateTime.UtcNow,
            DeviceStates = DeviceStateManager.GetSnapshot(),
            TaskRuntimes = TaskStateManager.GetSnapshot(),
            AlarmStates = AlarmStateManager.GetSnapshot(),
            ObjectStates = ObjectStateManager.GetSnapshot(),
            PlcBlockStates = PlcBlockStateManager.GetSnapshot()
        };
    }

    public void RestoreFromSnapshot(StateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Clear();
        DeviceStateManager.RestoreFromSnapshot(snapshot.DeviceStates);
        TaskStateManager.RestoreFromSnapshot(snapshot.TaskRuntimes);
        AlarmStateManager.RestoreFromSnapshot(snapshot.AlarmStates);
        ObjectStateManager.RestoreFromSnapshot(snapshot.ObjectStates);
        PlcBlockStateManager.RestoreFromSnapshot(snapshot.PlcBlockStates);
    }

    public void Clear()
    {
        DeviceStateManager.Clear();
        TaskStateManager.Clear();
        AlarmStateManager.Clear();
        ObjectStateManager.Clear();
        PlcBlockStateManager.Clear();
    }

    // ==================== 批量更新 ====================

    public IBatchScope BeginBatch() => BatchScope.Begin();

    // ==================== Per-key 订阅 ====================

    public IDisposable WatchDevice(string deviceId, Action<DeviceState> handler)
        => DeviceStateManager.Watch(deviceId, handler);

    public IDisposable WatchTask(string taskId, Action<TaskRuntime> handler)
        => TaskStateManager.Watch(taskId, handler);

    public IDisposable WatchAlarm(string alarmId, Action<AlarmState> handler)
        => AlarmStateManager.Watch(alarmId, handler);

    public IDisposable WatchObject(string objectId, Action<ObjectState> handler)
        => ObjectStateManager.Watch(objectId, handler);

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
}
