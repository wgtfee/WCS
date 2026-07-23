namespace Wcs.Core.EventDetection;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.SignalSnapshot;
using Wcs.Core.StateCenter.Interfaces;

public class EventDetector
{
    private readonly IEventBus _eventBus;
    private readonly SignalSnapshotCenter _snapshotCenter;
    private readonly IStateCenter _stateCenter;
    private readonly List<ISignalValidator> _validators = new();
    private readonly List<EventDetectionRule> _extraRules = new();
    private readonly ILogger<EventDetector>? _logger;
    public bool EnableNamingConvention { get; set; } = true;

    public EventDetector(IEventBus eventBus, SignalSnapshotCenter snapshotCenter,
        IStateCenter stateCenter, ILogger<EventDetector>? logger = null)
    {
        _eventBus = eventBus;
        _snapshotCenter = snapshotCenter;
        _stateCenter = stateCenter;
        _logger = logger;
    }

    public void RegisterValidator(ISignalValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    public void RegisterRule(EventDetectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _extraRules.Add(rule);
    }

    public void Detect(string blockKey, object current, string plcName = "", int dbBlock = 0)
        => DetectAsync(blockKey, current, plcName, dbBlock).GetAwaiter().GetResult();

    public async Task DetectAsync(
        string blockKey,
        object current,
        string plcName = "",
        int dbBlock = 0,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshotCenter.Get(blockKey);
        var previous = snapshot?.Previous;
        if (previous == null) return;

        var structType = current.GetType();
        var fields = FieldMetadataCache.GetFields(structType);

        foreach (var meta in fields)
        {
            var newVal = FieldMetadataCache.GetValue(meta, current);
            var oldVal = FieldMetadataCache.GetValue(meta, previous);
            if (Equals(newVal, oldVal)) continue;

            var edge = DetectEdge(oldVal, newVal);

            var rawSignal = new RawSignalEvent
            {
                PlcName = plcName, DbBlock = dbBlock, FieldName = meta.FieldName,
                OldValue = oldVal?.ToString(), NewValue = newVal?.ToString(), Edge = edge.ToString()
            };

            // 验证管道 — 现在 stateCenter 不为 null 了
            var ctx = new ValidatorContext(_stateCenter, rawStruct: current, previousStruct: previous);
            var rejected = false;
            SignalValidationResult? commandResult = null;

            foreach (var v in _validators)
            {
                var vr = v.Validate(ctx);
                if (vr != null && vr.Action == SignalValidationAction.Reject)
                {
                    rejected = true;
                    rawSignal.ValidatorPassed = false;
                    rawSignal.ValidatorReason = vr.Reason;
                    _logger?.LogInformation("[Detector] ❌ {Field} 拒绝: {Reason}", meta.FieldName, vr.Reason);
                    break;
                }
                if (vr?.Command != null)
                    commandResult = vr;
            }

            if (!rejected)
            {
                rawSignal.ValidatorPassed = true;
                var domainEvent = CreateDomainEvent(meta, newVal, oldVal, edge);
                if (domainEvent != null)
                {
                    rawSignal.DomainEventType = domainEvent.GetType().Name;
                    await _eventBus.PublishAsync(domainEvent, cancellationToken)
                        .ConfigureAwait(false);
                }

                // 验证通过 + 有命令 → 发布命令请求事件，自动写入 PLC
                if (commandResult != null)
                {
                    var cmdEvent = new CommandRequestedEvent
                    {
                        Command = commandResult.Command!,
                        CommandType = commandResult.CommandType ?? meta.FieldName,
                        DeviceId = commandResult.TargetDeviceId ?? meta.DeviceId ?? "",
                    };
                    await _eventBus.PublishAsync(cmdEvent, cancellationToken)
                        .ConfigureAwait(false);
                    _logger?.LogInformation("[Detector] ⚡ {Field} 验证通过 → 发命令 {Cmd}",
                        meta.FieldName, commandResult.CommandType);
                }
            }
            //验证成功的数据进行推送
            await _eventBus.PublishAsync(rawSignal, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private IEvent? CreateDomainEvent(FieldMetadata meta, object? newVal, object? oldVal, EdgeType edge)
    {
        foreach (var rule in _extraRules)
        {
            if (rule.FieldName != meta.FieldName) continue;
            if (rule.Edge != EdgeType.Both && rule.Edge != edge) continue;
            return CreateEventFromRule(rule, meta);
        }
        if (!EnableNamingConvention) return null;
        if (newVal is not bool newBool || oldVal is not bool oldBool) return null;
        if (meta.DeviceId == null) return null;

        var suffix = meta.Suffix;

        // 到货/出库请求只在 0→1 上升沿创建一次业务任务。
        // 原实现下降沿也发布 PalletArrivedEvent，使一个脉冲被重复计成两次任务。
        if (suffix is "_ARRIVED" or "_REQUESTOUT")
        {
            if (edge == EdgeType.Rising && newBool)
                return new PalletArrivedEvent { DeviceId = meta.DeviceId };
            return null;
        }

        if (suffix is "_FAULT" or "_ARALM")
        {
            // 上升沿：报警
            if (!oldBool && newBool)
            {
                return new DeviceFaultEvent
                {
                    DeviceId = meta.DeviceId,
                    FaultCode = meta.FieldName
                };
            }

            // 下降沿：恢复
            if (oldBool && !newBool)
            {
                return new DeviceRecoveredEvent
                {
                    DeviceId = meta.DeviceId,
                    FaultCode = meta.FieldName
                };
            }
        }

        if (suffix == "_READY")
            return new ConveyorReadyChangedEvent { DeviceId = meta.DeviceId, Ready = newBool };
        return null;
    }

    private IEvent? CreateEventFromRule(EventDetectionRule rule, FieldMetadata meta)
    {
        if (string.IsNullOrEmpty(rule.TargetEventType))
            return CreateDomainEvent(meta, true, null, EdgeType.Rising);
        var eventType = Type.GetType(rule.TargetEventType);
        if (eventType == null || !typeof(IEvent).IsAssignableFrom(eventType)) return null;
        try
        {
            var evt = (IEvent)Activator.CreateInstance(eventType)!;
            var devProp = eventType.GetProperty("DeviceId");
            if (devProp != null && rule.DeviceId != null) devProp.SetValue(evt, rule.DeviceId);
            if (rule.PropertyMappings != null)
                foreach (var kvp in rule.PropertyMappings)
                {
                    var prop = eventType.GetProperty(kvp.Key);
                    if (prop != null) prop.SetValue(evt, kvp.Value);
                }
            return evt;
        }
        catch { return null; }
    }

    private static EdgeType DetectEdge(object? oldVal, object? newVal)
    {
        if (oldVal is bool ob && newVal is bool nb)
        {
            if (!ob && nb) return EdgeType.Rising;
            if (ob && !nb) return EdgeType.Falling;
        }
        return EdgeType.Both;
    }
}
