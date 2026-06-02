namespace Wcs.Core.StateCenter.Interfaces;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// StateCenter 接口 - 系统实时状态中心
/// </summary>
public interface IStateCenter
{
    /// <summary>
    /// 更新设备状态
    /// </summary>
    void UpdateDeviceState(string deviceId, DeviceState state);

    /// <summary>
    /// 获取设备状态
    /// </summary>
    DeviceState? GetDeviceState(string deviceId);

    /// <summary>
    /// 获取所有设备状态
    /// </summary>
    IEnumerable<DeviceState> GetAllDeviceStates();

    /// <summary>
    /// 更新任务运行时
    /// </summary>
    void UpdateTaskRuntime(string taskId, TaskRuntime runtime);

    /// <summary>
    /// 获取任务运行时
    /// </summary>
    TaskRuntime? GetTaskRuntime(string taskId);

    /// <summary>
    /// 获取所有活跃任务
    /// </summary>
    IEnumerable<TaskRuntime> GetAllActiveTasks();

    /// <summary>
    /// 更新报警状态
    /// </summary>
    void UpdateAlarmState(string alarmId, AlarmState state);

    /// <summary>
    /// 获取报警状态
    /// </summary>
    AlarmState? GetAlarmState(string alarmId);

    /// <summary>
    /// 获取所有活跃报警
    /// </summary>
    IEnumerable<AlarmState> GetActiveAlarms();

    /// <summary>
    /// 更新物体状态
    /// </summary>
    void UpdateObjectState(string objectId, ObjectState state);

    /// <summary>
    /// 获取物体状态
    /// </summary>
    ObjectState? GetObjectState(string objectId);

    /// <summary>
    /// 更新PLC数据块状态
    /// </summary>
    void UpdatePlcBlockState(string blockName, PlcBlockState state);

    /// <summary>
    /// 获取PLC数据块状态
    /// </summary>
    PlcBlockState? GetPlcBlockState(string blockName);

    /// <summary>
    /// 清空所有状态（恢复时使用）
    /// </summary>
    void Clear();

    /// <summary>
    /// 获取状态快照（用于恢复）
    /// </summary>
    StateSnapshot GetSnapshot();

    /// <summary>
    /// 从快照恢复状态
    /// </summary>
    void RestoreFromSnapshot(StateSnapshot snapshot);
}

/// <summary>
/// 状态快照 - 用于系统恢复
/// </summary>
public class StateSnapshot
{
    public DateTime SnapshotTime { get; set; }

    public Dictionary<string, DeviceState> DeviceStates { get; set; } = new();

    public Dictionary<string, TaskRuntime> TaskRuntimes { get; set; } = new();

    public Dictionary<string, AlarmState> AlarmStates { get; set; } = new();

    public Dictionary<string, ObjectState> ObjectStates { get; set; } = new();

    public Dictionary<string, PlcBlockState> PlcBlockStates { get; set; } = new();
}

/// <summary>
/// 状态变化监听器
/// </summary>
public interface IStateChangeListener
{
    /// <summary>
    /// 设备状态变化回调
    /// </summary>
    void OnDeviceStateChanged(string deviceId, DeviceState oldState, DeviceState newState);

    /// <summary>
    /// 任务状态变化回调
    /// </summary>
    void OnTaskStateChanged(string taskId, TaskRuntime oldRuntime, TaskRuntime newRuntime);

    /// <summary>
    /// 报警状态变化回调
    /// </summary>
    void OnAlarmStateChanged(string alarmId, AlarmState oldState, AlarmState newState);
}
