namespace Wcs.Core.StateMachine;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// 状态机接口 - 统一状态转移引擎
/// </summary>
public interface IStateMachine<TState> where TState : Enum
{
    /// <summary>
    /// 当前状态
    /// </summary>
    TState CurrentState { get; }

    /// <summary>
    /// 尝试转移到目标状态
    /// </summary>
    bool TryTransitionTo(TState target, out string? reason);

    /// <summary>
    /// 检查是否允许转移到目标状态
    /// </summary>
    bool CanTransitionTo(TState target);

    /// <summary>
    /// 获取所有允许的转移
    /// </summary>
    IEnumerable<TState> GetAllowedTransitions();

    /// <summary>
    /// 注册转移回调
    /// </summary>
    void OnTransition(Action<TState, TState> callback);

    /// <summary>
    /// 重置到指定状态
    /// </summary>
    void Reset(TState state);
}

/// <summary>
/// 任务状态机 - 基于 TaskStatusEnum
/// </summary>
public interface ITaskStateMachine : IStateMachine<TaskStatusEnum>
{
}

/// <summary>
/// 设备状态机 - 基于 DeviceStatusEnum
/// </summary>
public interface IDeviceStateMachine : IStateMachine<DeviceStatusEnum>
{
}
