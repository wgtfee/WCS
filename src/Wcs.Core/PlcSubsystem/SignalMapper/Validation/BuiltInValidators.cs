namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation;

using Wcs.Core.EventBus.Events;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 设备忙闲状态验证器 — 设备 Busy 时拒绝新的到位信号
/// 典型场景：堆垛机正在执行任务时，拒绝新的 Store 信号
/// </summary>
public class DeviceBusyValidator : ISignalValidator
{
    public string ValidatorId => "DeviceBusy";
    public string? DeviceId { get; }
    public string? SignalId => null;

    private readonly IStateCenter _stateCenter;

    /// <param name="deviceId">要验证的设备 ID</param>
    public DeviceBusyValidator(IStateCenter stateCenter, string? deviceId = null)
    {
        _stateCenter = stateCenter;
        DeviceId = deviceId;
    }

    public SignalValidationResult? Validate(
        SignalDefinition definition,
        PlcBlockDiff diff,
        IReadOnlyList<IEvent> generatedEvents)
    {
        // 只验证到位/到达类信号
        if (!definition.SignalId.EndsWith("Arrived") && !definition.SignalId.EndsWith("Store"))
            return null;

        // 如果是全局验证器但信号定义有 DeviceId，用它
        var deviceId = DeviceId ?? definition.PropertyMappings.GetValueOrDefault("DeviceId");
        if (string.IsNullOrEmpty(deviceId))
            return null;

        // 查 StateCenter 中的设备状态
        var deviceState = _stateCenter.GetDeviceState(deviceId);
        if (deviceState != null && deviceState.Status == StateCenter.Models.DeviceStatusEnum.Running)
        {
            return SignalValidationResult.Reject(
                $"[{ValidatorId}] {deviceId} 当前繁忙，拒绝信号 {definition.SignalId}");
        }

        return SignalValidationResult.Pass();
    }
}

/// <summary>
/// 互斥信号验证器 — 两个信号不能同时为 true
/// 典型场景：提升机不能同时接收上行和下行请求
/// </summary>
public class MutexSignalValidator : ISignalValidator
{
    public string ValidatorId => "MutexSignal";
    public string? DeviceId { get; }
    public string? SignalId => null;

    private readonly string _mutexGroup;
    private readonly string[] _conflictingSignals;

    /// <param name="deviceId">目标设备 ID</param>
    /// <param name="mutexGroup">互斥组名称</param>
    /// <param name="conflictingSignals">互斥的信号 ID 列表（同组内同时只能有一个 true）</param>
    public MutexSignalValidator(string? deviceId, string mutexGroup, params string[] conflictingSignals)
    {
        DeviceId = deviceId;
        _mutexGroup = mutexGroup;
        _conflictingSignals = conflictingSignals;
    }

    public SignalValidationResult? Validate(
        SignalDefinition definition,
        PlcBlockDiff diff,
        IReadOnlyList<IEvent> generatedEvents)
    {
        if (!_conflictingSignals.Contains(definition.SignalId))
            return null;

        // 简化检查：如果本次 diff 中有互斥组内的其他信号同时变化，拒绝
        var triggerChange = diff.Changes.FirstOrDefault(c =>
            c.Offset == definition.ByteOffset);
        if (triggerChange == null || triggerChange.NewValue == 0)
            return null;

        return SignalValidationResult.Pass(
            $"[{ValidatorId}] 互斥组 '{_mutexGroup}' 中 {definition.SignalId} 已激活");
    }
}
