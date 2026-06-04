namespace Wcs.Core.EventDetection;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.SignalSnapshot;

/// <summary>
/// 事件检测器 — 两级事件管线
///
/// 第一级：RawSignalEvent（原始 PLC 信号变化，始终发布，用于审计/Trace）
/// 第二级：DomainEvent（验证通过后的业务事件，触发 RuleEngine）
///
/// 流程：
///   PLC 字段变化
///     ↓
///   RawSignalEvent ──→ TraceCenter 记录
///     ↓
///   Validator 管道
///     ├─ Pass   → RawSignalEvent.ValidatorPassed=true
///     │           → DomainEvent（PalletArrivedEvent / DeviceFaultEvent）
///     │           → EventBus → RuleEngine → TaskGenerator
///     │
///     └─ Reject → RawSignalEvent.ValidatorPassed=false
///                   → 仅记录，不发布 DomainEvent（StateCenter 不受影响）
/// </summary>
public class EventDetector
{
    private readonly IEventBus _eventBus;
    private readonly SignalSnapshotCenter _snapshotCenter;
    private readonly List<ISignalValidator> _validators = new();
    private readonly List<EventDetectionRule> _extraRules = new();
    private readonly ILogger<EventDetector>? _logger;
    public bool EnableNamingConvention { get; set; } = true;

    public EventDetector(IEventBus eventBus, SignalSnapshotCenter snapshotCenter,
        ILogger<EventDetector>? logger = null)
    {
        _eventBus = eventBus;
        _snapshotCenter = snapshotCenter;
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

    /// <summary>检测边沿变化 — 两级事件管线</summary>
    public void Detect(string blockKey, object current, string plcName = "", int dbBlock = 0)
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

            // ===== 第一级：始终发布 RawSignalEvent =====
            var rawSignal = new RawSignalEvent
            {
                PlcName = plcName,
                DbBlock = dbBlock,
                FieldName = meta.FieldName,
                OldValue = oldVal?.ToString(),
                NewValue = newVal?.ToString(),
                Edge = edge.ToString()
            };

            // ===== 验证管道 =====
            var ctx = new ValidatorContext(null!, rawStruct: current, previousStruct: previous);
            var rejected = false;

            foreach (var v in _validators)
            {
                var vr = v.Validate(ctx);
                if (vr != null && vr.Action == SignalValidationAction.Reject)
                {
                    rejected = true;
                    rawSignal.ValidatorPassed = false;
                    rawSignal.ValidatorReason = vr.Reason;
                    _logger?.LogInformation("[Detector] ❌ {Field} 验证拒绝: {Reason}",
                        meta.FieldName, vr.Reason);
                    break;
                }
            }

            if (!rejected)
            {
                rawSignal.ValidatorPassed = true;

                // ===== 第二级：验证通过 → 发布 DomainEvent =====
                var domainEvent = CreateDomainEvent(meta, newVal, edge);
                if (domainEvent != null)
                {
                    rawSignal.DomainEventType = domainEvent.GetType().Name;
                    _eventBus.PublishAsync(domainEvent).GetAwaiter().GetResult();
                }
            }

            // 发布 RawSignalEvent（始终发布，供 TraceCenter 记录）
            _eventBus.PublishAsync(rawSignal).GetAwaiter().GetResult();
        }
    }

    /// <summary>按命名约定或精确规则创建领域事件</summary>
    private IEvent? CreateDomainEvent(FieldMetadata meta, object? newVal, EdgeType edge)
    {
        // 1. 精确规则匹配
        foreach (var rule in _extraRules)
        {
            if (rule.FieldName != meta.FieldName) continue;
            if (rule.Edge != EdgeType.Both && rule.Edge != edge) continue;
            return CreateEventFromRule(rule, meta);
        }

        // 2. 命名约定推断
        if (!EnableNamingConvention) return null;
        if (newVal is not bool boolVal || edge != EdgeType.Rising || !boolVal) return null;
        if (meta.DeviceId == null) return null;

        var suffix = meta.Suffix;
        if (suffix is "_ARRIVED" or "_REQUESTOUT")
            return new PalletArrivedEvent { DeviceId = meta.DeviceId };
        if (suffix == "_FAULT")
            return new DeviceFaultEvent { DeviceId = meta.DeviceId, FaultCode = meta.FieldName };
        if (suffix == "_READY")
            return new ConveyorReadyChangedEvent { DeviceId = meta.DeviceId, Ready = true };

        return null;
    }

    private IEvent? CreateEventFromRule(EventDetectionRule rule, FieldMetadata meta)
    {
        if (string.IsNullOrEmpty(rule.TargetEventType))
            return CreateDomainEvent(meta, true, EdgeType.Rising);

        var eventType = Type.GetType(rule.TargetEventType);
        if (eventType == null || !typeof(IEvent).IsAssignableFrom(eventType)) return null;

        try
        {
            var evt = (IEvent)Activator.CreateInstance(eventType)!;
            var devProp = eventType.GetProperty("DeviceId");
            if (devProp != null && rule.DeviceId != null) devProp.SetValue(evt, rule.DeviceId);
            if (rule.PropertyMappings != null)
            {
                foreach (var kvp in rule.PropertyMappings)
                {
                    var prop = eventType.GetProperty(kvp.Key);
                    if (prop != null) prop.SetValue(evt, kvp.Value);
                }
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
