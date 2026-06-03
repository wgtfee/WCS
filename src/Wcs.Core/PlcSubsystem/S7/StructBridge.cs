using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.StateCenter.Interfaces;

namespace Wcs.Core.PlcSubsystem.S7;

/// <summary>
/// 结构体桥接器 — S7 struct 字段级变化 → 验证管道 → EventBus
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

    /// <summary>
    /// 处理结构体变化 — Struct Diff → 验证管道 → 通过后发布事件
    /// 返回结果中包含 PreviousStruct 字段，调用方用它更新缓存
    /// </summary>
    public StructBridgeResult Process<T>(string blockName, T? previous, T current) where T : class
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
                _logger?.LogInformation("[StructBridge] ❌ {Block}.{Field}: {Old}→{New} 拒绝 ({Reason})",
                    blockName, change.FieldName, change.OldValue, change.NewValue, vr.Reason);
                continue;
            }
            result.AcceptedChanges++;
            _eventBus.PublishAsync(new FieldChangedEvent
            {
                BlockName = blockName,
                FieldName = change.FieldName,
                OldValue = change.OldValue?.ToString(),
                NewValue = change.NewValue?.ToString()
            });
        }

        if (result.AcceptedChanges > 0)
            _logger?.LogInformation("[StructBridge] ✅ {Block}: {Accepted}/{Total} 字段变化已通过",
                blockName, result.AcceptedChanges, result.TotalChanges);

        return result;
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
    /// <summary>当前结构体（调用方用它更新 previous 缓存）</summary>
    public object? PreviousStruct { get; set; }
}

/// <summary>字段变化事件（StructBridge 发布到 EventBus 的事件）</summary>
public class FieldChangedEvent : EventBus.Events.EventBase
{
    public string BlockName { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
