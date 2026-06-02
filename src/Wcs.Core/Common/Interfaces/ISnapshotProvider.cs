namespace Wcs.Core.Common.Interfaces;

/// <summary>
/// 模块级快照提供者 — 每个核心模块实现此接口以支持系统级恢复
/// </summary>
public interface ISnapshotProvider
{
    string ModuleName { get; }

    /// <summary>
    /// 捕获当前模块的状态快照（返回模块自有类型）
    /// </summary>
    Task<object> CaptureSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// 从快照恢复模块状态（重建所有内部索引）
    /// </summary>
    Task RestoreSnapshotAsync(object snapshot, CancellationToken ct = default);
}
