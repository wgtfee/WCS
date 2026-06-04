namespace Wcs.Core.EventDetection;

using Microsoft.Extensions.Logging;
using System.Reflection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 事件检测器 — PLC 状态变化 → 业务事件的转换层
///
/// 这是 PLC 世界与 Task 世界的桥梁。
///
/// 职责：
///   1. 接收 DeviceStateChangedEvent（来自 S7PollingService）
///   2. 从 ValidatorContext 中拿到 struct 字段的 old/new 值
///   3. 检测边沿变化（false→true, true→false）
///   4. 通过命名约定或配置规则生成业务事件
///   5. 经过验证管道 → 发布到 EventBus → RuleEngine 消费
///
/// 解决了两个核心问题：
///   - "Running 不应该触发任务" → 只有边沿变化才产生事件
///   - "几百个工位怎么配" → 命名约定自动推断，无需逐条配置
///
/// 命名约定规则（文件名 = 字段名后缀 = 事件类型）：
///   ├─ _Arrived / _RequestOut → 上升沿 → PalletArrivedEvent
///   ├─ _Fault               → 上升沿 → DeviceFaultEvent
///   ├─ _Ready               → 上升沿 → ConveyorReadyEvent
///   ├─ _Speed / _Count       → 任何变化 → 值变化事件
///   └─ 其他 bool 字段         → 上升沿 → DeviceStateChangedEvent（通用）
/// </summary>
public class EventDetector
{
    private readonly IEventBus _eventBus;
    private readonly List<ISignalValidator> _validators = new();
    private readonly List<EventDetectionRule> _extraRules = new();
    private readonly ILogger<EventDetector>? _logger;

    /// <summary>是否启用命名约定推断（默认 true）</summary>
    public bool EnableNamingConvention { get; set; } = true;

    public EventDetector(IEventBus eventBus, ILogger<EventDetector>? logger = null)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>注册验证器</summary>
    public void RegisterValidator(ISignalValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    /// <summary>注册精确匹配规则</summary>
    public void RegisterRule(EventDetectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _extraRules.Add(rule);
    }

    /// <summary>
    /// 检测并生成业务事件 — 由 S7PollingService 在每次 struct 变化后调用
    /// </summary>
    /// <param name="structType">结构体类型</param>
    /// <param name="current">当前读取的结构体</param>
    /// <param name="previous">上一次读取的结构体</param>
    public void DetectAndPublish(Type structType, object current, object? previous)
    {
        var fields = structType.GetFields(BindingFlags.Public | BindingFlags.Instance);

        foreach (var field in fields)
        {
            var newVal = field.GetValue(current);
            if (previous == null) continue;
            var oldVal = field.GetValue(previous);
            if (Equals(newVal, oldVal)) continue;

            // 检测边沿
            var edge = DetectEdge(oldVal, newVal);

            // 1. 精确匹配规则
            foreach (var rule in _extraRules)
            {
                if (rule.FieldName != field.Name) continue;
                if (rule.Edge != EdgeType.Both && rule.Edge != edge) continue;

                var evt = CreateEventFromRule(rule, field.Name, newVal);
                if (evt != null)
                    PublishWithValidation(evt, current, previous, structType);
            }

            // 2. 命名约定推断
            if (EnableNamingConvention)
            {
                var conventionEvent = CreateEventByConvention(field.Name, newVal, edge, current);
                if (conventionEvent != null)
                    PublishWithValidation(conventionEvent, current, previous, structType);
            }
        }
    }

    /// <summary>
    /// 发布前经过验证管道
    /// </summary>
    private void PublishWithValidation(IEvent evt, object current, object? previous, Type structType)
    {
        // 构建验证器上下文（注意：这里只做事件级验证，不阻断 StateCenter 更新）
        var ctx = new ValidatorContext(
            null!, // 验证器不应通过这里查 StateCenter，StateCenter 已经更新了
            rawStruct: current,
            previousStruct: previous
        );

        // 验证管道
        foreach (var v in _validators)
        {
            var vr = v.Validate(ctx);
            if (vr != null && vr.Action == SignalValidationAction.Reject)
            {
                _logger?.LogInformation("[EventDetector] ❌ {Event} 被验证器拒绝: {Reason}",
                    evt.GetType().Name, vr.Reason);
                return; // 事件被拦截，StateCenter 已更新不受影响
            }
        }

        // 发布业务事件 → RuleEngine 消费
        _eventBus.PublishAsync(evt);
        _logger?.LogDebug("[EventDetector] ✅ {Event}", evt.GetType().Name);
    }

    /// <summary>
    /// 按命名约定推断事件类型
    /// </summary>
    private IEvent? CreateEventByConvention(string fieldName, object? newVal, EdgeType edge, object current)
    {
        if (newVal is not bool boolVal) return null;
        var name = fieldName.ToUpperInvariant();

        // 只处理上升沿（下降沿通常不代表"事件发生"）
        if (edge != EdgeType.Rising) return null;
        if (!boolVal) return null;

        var deviceId = ExtractDeviceId(fieldName);
        if (deviceId == null) return null;

        // 根据字段名后缀推断事件类型
        if (name.EndsWith("_ARRIVED") || name.EndsWith("_REQUESTOUT"))
        {
            _logger?.LogInformation("[EventDetector] ⚡ {Device} 托盘到位 → PalletArrivedEvent", deviceId);
            return new PalletArrivedEvent { DeviceId = deviceId };
        }

        if (name.EndsWith("_FAULT"))
        {
            _logger?.LogWarning("[EventDetector] ⚡ {Device} 故障 → DeviceFaultEvent", deviceId);
            return new DeviceFaultEvent { DeviceId = deviceId, FaultCode = fieldName };
        }

        if (name.EndsWith("_READY"))
        {
            return new ConveyorReadyChangedEvent { DeviceId = deviceId, Ready = true };
        }

        // 通用：其他 bool 字段的上升沿 → 通用状态变化事件
        return null;
    }

    /// <summary>
    /// 按精确规则创建事件
    /// </summary>
    private IEvent? CreateEventFromRule(EventDetectionRule rule, string fieldName, object? newVal)
    {
        if (string.IsNullOrEmpty(rule.TargetEventType))
        {
            // 没有指定事件类型，走命名约定
            return CreateEventByConvention(fieldName, newVal, rule.Edge, null!);
        }

        var eventType = Type.GetType(rule.TargetEventType);
        if (eventType == null || !typeof(IEvent).IsAssignableFrom(eventType)) return null;

        IEvent evt;
        try { evt = (IEvent)Activator.CreateInstance(eventType)!; } catch { return null; }

        if (evt is EventBase eb)
        {
            // 设置 DeviceId
            var devProp = eventType.GetProperty("DeviceId");
            if (devProp != null && rule.DeviceId != null)
                devProp.SetValue(eb, rule.DeviceId);

            // 设置额外属性
            if (rule.PropertyMappings != null)
            {
                foreach (var kvp in rule.PropertyMappings)
                {
                    var prop = eventType.GetProperty(kvp.Key);
                    if (prop != null)
                        prop.SetValue(eb, kvp.Value);
                }
            }
        }

        return evt;
    }

    private static EdgeType DetectEdge(object? oldVal, object? newVal)
    {
        if (oldVal is bool oldBool && newVal is bool newBool)
        {
            if (!oldBool && newBool) return EdgeType.Rising;
            if (oldBool && !newBool) return EdgeType.Falling;
        }
        return EdgeType.Both;
    }

    private static string? ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return null;
        var parts = fieldName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : null;
    }
}
