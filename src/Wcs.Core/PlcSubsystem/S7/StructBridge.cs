using Microsoft.Extensions.Logging;
using System.Reflection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

namespace Wcs.Core.PlcSubsystem.S7;

/// <summary>
/// 结构体桥接器 — S7 struct 字段变化 → 验证管道 → StateCenter 更新 + EventBus
///
/// 每次字段变化触发：
/// 1. 验证管道 (ISignalValidator)
/// 2. StateCenter.UpdateDeviceState() ← 关键：让状态中心知道设备最新状态
/// 3. DeviceStateChangedEvent → EventBus → 其他模块（WaitNode/UI）收到通知
/// 4. FieldChangedEvent → EventBus → 审计/持久化
/// </summary>
public class StructBridge
{
    private readonly IStateCenter _stateCenter;
    private readonly IEventBus _eventBus;
    private readonly List<ISignalValidator> _validators = new();
    private readonly ILogger<StructBridge>? _logger;

    public StructBridge(IStateCenter stateCenter, IEventBus eventBus, ILogger<StructBridge>? logger = null)
    {
        _stateCenter = stateCenter;
        _eventBus = eventBus;
        _logger = logger;
    }

    public void RegisterValidator(ISignalValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    public void RegisterValidators(IEnumerable<ISignalValidator> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        _validators.AddRange(validators);
    }

    /// <summary>泛型处理（强类型，推荐）</summary>
    public async Task<StructBridgeResult> ProcessAsync<T>(string blockName, T? previous, T current) where T : class
    {
        var result = new StructBridgeResult { BlockName = blockName, PreviousStruct = current };
        var diff = StructDiffEngine.Compare(previous, current);
        if (!diff.HasChanges) return result;

        result.TotalChanges = diff.Changes.Count;
        result.HasChanges = true;

        foreach (var change in diff.Changes)
        {
            var ctx = new ValidatorContext(_stateCenter, rawStruct: current, previousStruct: previous);
            var vr = RunValidators(ctx);
            if (vr != null && vr.Action == SignalValidationAction.Reject)
            {
                result.RejectedChanges++;
                _logger?.LogInformation("[Bridge] ❌ {Block}.{Field} 拒绝: {Reason}",
                    blockName, change.FieldName, vr.Reason);
                continue;
            }
            result.AcceptedChanges++;

            // 1. 从字段名提取设备 ID
            var deviceId = ExtractDeviceId(change.FieldName);
            var isBool = change.NewValue is bool;

            if (deviceId != null)
            {
                // 2. 更新 StateCenter（系统真理源）
                var newStatus = isBool && (bool)change.NewValue!
                    ? DeviceStatusEnum.Running
                    : DeviceStatusEnum.Idle;

                _stateCenter.UpdateDeviceState(deviceId, new DeviceState
                {
                    DeviceId = deviceId,
                    Status = newStatus,
                    LastUpdateTime = DateTime.UtcNow
                });

                // 3. 发布 DeviceStateChangedEvent → 通知 WaitNode、UI 等
                await _eventBus.PublishAsync(new DeviceStateChangedEvent
                {
                    DeviceId = deviceId,
                    OldStatus = isBool && change.OldValue is bool b && b
                        ? DeviceStatusEnum.Running : DeviceStatusEnum.Idle,
                    NewStatus = newStatus
                });
            }

            // 4. 发布 FieldChangedEvent → 审计/持久化
            await _eventBus.PublishAsync(new FieldChangedEvent
            {
                BlockName = blockName, FieldName = change.FieldName,
                OldValue = change.OldValue?.ToString(), NewValue = change.NewValue?.ToString()
            });
        }

        if (result.AcceptedChanges > 0)
            _logger?.LogInformation("[Bridge] ✅ {Block}: {Accepted}/{Total} → StateCenter + EventBus",
                blockName, result.AcceptedChanges, result.TotalChanges);

        return result;
    }

    /// <summary>非泛型入口（给 S7PollingService 用）</summary>
    public async Task<StructBridgeResult> ProcessUntypedAsync(string blockName, Type structType, object? previous, object current)
    {
        var method = typeof(StructBridge).GetMethod(nameof(ProcessAsync), BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ProcessAsync not found");
        var generic = method.MakeGenericMethod(structType);
        var task = (Task<StructBridgeResult>)generic.Invoke(this, new[] { blockName, previous, current })!;
        return await task;
    }

    private static string? ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return null;
        var parts = fieldName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : null;
    }

    private SignalValidationResult? RunValidators(ValidatorContext ctx)
    {
        foreach (var v in _validators)
        {
            var r = v.Validate(ctx);
            if (r != null && r.Action != SignalValidationAction.Pass) return r;
        }
        return null;
    }
}

public class StructBridgeResult
{
    public string BlockName { get; set; } = string.Empty;
    public bool HasChanges { get; set; }
    public int TotalChanges { get; set; }
    public int AcceptedChanges { get; set; }
    public int RejectedChanges { get; set; }
    public object? PreviousStruct { get; set; }
}

public class FieldChangedEvent : EventBase
{
    public string BlockName { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
