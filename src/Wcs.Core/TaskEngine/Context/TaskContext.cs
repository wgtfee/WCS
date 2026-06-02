namespace Wcs.Core.TaskEngine.Context;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// 任务上下文 - 统一的任务信息载体
/// </summary>
public class TaskContext
{
    /// <summary>
    /// 任务唯一标识
    /// </summary>
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 目标设备 ID
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// 任务状态
    /// </summary>
    public TaskStatusEnum Status { get; set; } = TaskStatusEnum.Created;

    /// <summary>
    /// 优先级 (0-4, 4最高)
    /// </summary>
    public int Priority { get; set; } = 2;

    /// <summary>
    /// 路由 ID
    /// </summary>
    public string RouteId { get; set; } = string.Empty;

    /// <summary>
    /// 任务参数
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// 最大重试次数
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// 任务创建时间
    /// </summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 任务启动时间
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// 任务完成时间
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 任务超时（毫秒）
    /// </summary>
    public int TimeoutMs { get; set; } = 30000;

    /// <summary>
    /// 任务执行结果
    /// </summary>
    public object? Result { get; set; }

    /// <summary>
    /// 任务错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 是否可重试
    /// </summary>
    public bool IsRetryable { get; set; } = true;

    /// <summary>
    /// 关联的父任务 ID
    /// </summary>
    public string? ParentTaskId { get; set; }

    /// <summary>
    /// 依赖的任务 ID 列表
    /// </summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>
    /// 任务标签
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// 获取任务执行时长
    /// </summary>
    public long GetElapsedMilliseconds()
    {
        if (StartTime == null || EndTime == null)
            return 0;
        return (long)(EndTime.Value - StartTime.Value).TotalMilliseconds;
    }

    /// <summary>
    /// 检查任务是否已超时
    /// </summary>
    public bool IsTimeout()
    {
        if (StartTime == null)
            return false;
        return (DateTime.UtcNow - StartTime.Value).TotalMilliseconds > TimeoutMs;
    }

    /// <summary>
    /// 克隆任务
    /// </summary>
    public TaskContext Clone()
    {
        return new TaskContext
        {
            TaskId = this.TaskId,
            DeviceId = this.DeviceId,
            Status = TaskStatusEnum.Created,
            Priority = this.Priority,
            RouteId = this.RouteId,
            Parameters = new Dictionary<string, object>(this.Parameters),
            RetryCount = this.RetryCount + 1,
            MaxRetries = this.MaxRetries,
            TimeoutMs = this.TimeoutMs,
            IsRetryable = this.IsRetryable,
            ParentTaskId = this.ParentTaskId,
            DependsOn = new List<string>(this.DependsOn),
            Tags = new Dictionary<string, string>(this.Tags)
        };
    }
}
