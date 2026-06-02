namespace Wcs.Core.EventBus.Persistence;

using Wcs.Core.EventBus.Events;

/// <summary>
/// 事件存储接口 — 持久化事件用于故障恢复后重放
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// 追加写入事件
    /// </summary>
    Task AppendAsync(IEvent @event, CancellationToken ct = default);

    /// <summary>
    /// 按时间范围查询事件
    /// </summary>
    Task<IReadOnlyList<IEvent>> QueryAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>
    /// 获取最近的 N 条事件
    /// </summary>
    Task<IReadOnlyList<IEvent>> GetLatestAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// 清理超过指定期限的事件文件
    /// </summary>
    Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken ct = default);
}
