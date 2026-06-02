namespace Wcs.Core.ObjectTracking.Models;

/// <summary>
/// 移动类型
/// </summary>
public enum MovementType
{
    Entered,     // 物体进入系统
    Moved,       // 物体在系统内移动
    Exited,      // 物体离开系统
    Transferred  // 物体在不同设备间转移
}

/// <summary>
/// 物体移动记录 — 记录每次位置变更的完整信息
/// </summary>
public record MovementRecord
{
    /// <summary>物体 ID</summary>
    public string ObjectId { get; init; } = string.Empty;

    /// <summary>起始位置</summary>
    public Location From { get; init; } = new();

    /// <summary>目标位置</summary>
    public Location To { get; init; } = new();

    /// <summary>移动时间</summary>
    public DateTime MoveTime { get; init; } = DateTime.UtcNow;

    /// <summary>触发该移动的任务 ID（可选）</summary>
    public string? TriggeredByTaskId { get; init; }

    /// <summary>移动类型</summary>
    public MovementType Type { get; init; } = MovementType.Moved;
}
