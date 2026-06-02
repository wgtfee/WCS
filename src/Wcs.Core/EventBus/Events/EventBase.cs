namespace Wcs.Core.EventBus.Events;

/// <summary>
/// 事件优先级枚举
/// </summary>
public enum EventPriority
{
    /// <summary>
    /// 低优先级 - 统计、日志等
    /// </summary>
    Low = 0,

    /// <summary>
    /// 中优先级 - 常规业务事件
    /// </summary>
    Medium = 1,

    /// <summary>
    /// 高优先级 - 任务完成、状态变化
    /// </summary>
    High = 2,

    /// <summary>
    /// 紧急 - 系统错误、报警
    /// </summary>
    Critical = 3
}

/// <summary>
/// 事件基接口
/// </summary>
public interface IEvent
{
    /// <summary>
    /// 事件 ID（唯一标识）
    /// </summary>
    string EventId { get; }

    /// <summary>
    /// 事件发生时间
    /// </summary>
    DateTime OccurTime { get; }

    /// <summary>
    /// 事件优先级
    /// </summary>
    EventPriority Priority { get; }

    /// <summary>
    /// 事件来源
    /// </summary>
    string Source { get; }
}

/// <summary>
/// 事件基类
/// </summary>
public abstract class EventBase : IEvent
{
    public string EventId { get; } = Guid.NewGuid().ToString("N");

    public DateTime OccurTime { get; } = DateTime.UtcNow;

    public virtual EventPriority Priority => EventPriority.Medium;

    public virtual string Source => this.GetType().Name;
}

/// <summary>
/// 系统事件基类 - 包含处理结果
/// </summary>
public abstract class SystemEventBase : EventBase
{
    /// <summary>
    /// 事件是否已处理
    /// </summary>
    public bool IsHandled { get; set; }

    /// <summary>
    /// 处理结果
    /// </summary>
    public object? HandlerResult { get; set; }
}
