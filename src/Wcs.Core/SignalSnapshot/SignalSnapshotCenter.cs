namespace Wcs.Core.SignalSnapshot;

using System.Collections.Concurrent;

/// <summary>
/// PLC 块快照 — 一个 DB 块对应一个快照
/// </summary>
public class BlockSnapshot
{
    /// <summary>块标识（如 "PLC1.DB1"）</summary>
    public string BlockKey { get; set; } = string.Empty;
    /// <summary>当前读取的结构体</summary>
    public object? Current { get; set; }
    /// <summary>上一次读取的结构体</summary>
    public object? Previous { get; set; }
    /// <summary>最近一次变化时间</summary>
    public DateTime LastChanged { get; set; }
    /// <summary>变化版本号（每次变化递增）</summary>
    public long Version { get; set; }
    /// <summary>结构体类型</summary>
    public Type? StructType { get; set; }
}

/// <summary>
/// 信号快照中心 — 独立管理所有 PLC 块的 Current/Previous/Version
///
/// 从 S7PollingService 中剥离出来，让 EventDetector、Validator、TraceCenter 共享。
/// </summary>
public class SignalSnapshotCenter
{
    private readonly ConcurrentDictionary<string, BlockSnapshot> _snapshots = new();
    private long _globalVersion;

    /// <summary>
    /// 更新快照 — 返回 old/new 状态用于边沿检测
    /// </summary>
    public BlockSnapshot Update(string blockKey, object current, Type structType)
    {
        var snapshot = _snapshots.GetOrAdd(blockKey, _ => new BlockSnapshot
        {
            BlockKey = blockKey,
            StructType = structType
        });

        snapshot.Previous = snapshot.Current;
        snapshot.Current = current;
        snapshot.Version = Interlocked.Increment(ref _globalVersion);
        snapshot.LastChanged = DateTime.UtcNow;

        return snapshot;
    }

    /// <summary>获取快照</summary>
    public BlockSnapshot? Get(string blockKey) =>
        _snapshots.TryGetValue(blockKey, out var s) ? s : null;

    /// <summary>获取所有快照</summary>
    public IEnumerable<BlockSnapshot> GetAll() => _snapshots.Values;

    /// <summary>清空</summary>
    public void Clear() => _snapshots.Clear();
}
