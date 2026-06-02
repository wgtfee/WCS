namespace Wcs.Core.ResourceLock;

using System.Collections.Concurrent;

/// <summary>
/// 资源锁管理器接口 - 设备互斥锁
/// </summary>
public interface IResourceLockManager
{
    /// <summary>
    /// 尝试获取资源锁
    /// </summary>
    bool TryAcquire(string resourceId, string ownerId, int timeoutMs = 0);

    /// <summary>
    /// 释放资源锁
    /// </summary>
    void Release(string resourceId, string ownerId);

    /// <summary>
    /// 释放某拥有者的所有锁
    /// </summary>
    void ReleaseAll(string ownerId);

    /// <summary>
    /// 查询资源是否被锁定
    /// </summary>
    bool IsLocked(string resourceId);

    /// <summary>
    /// 获取锁的持有者
    /// </summary>
    string? GetOwner(string resourceId);

    /// <summary>
    /// 强制释放锁（人工介入）
    /// </summary>
    void ForceRelease(string resourceId);

    /// <summary>
    /// 获取所有锁状态
    /// </summary>
    Dictionary<string, string> GetAllLocks();

    /// <summary>
    /// 获取指定拥有者的锁列表
    /// </summary>
    IEnumerable<string> GetLocksByOwner(string ownerId);

    /// <summary>
    /// 清理过期锁
    /// </summary>
    int CleanupExpiredLocks(TimeSpan maxAge);
}

/// <summary>
/// 资源锁条目
/// </summary>
internal class LockEntry
{
    public string ResourceId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public DateTime AcquireTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 资源锁管理器实现 - 基于 ConcurrentDictionary
/// </summary>
public class ResourceLockManager : IResourceLockManager
{
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new();
    private readonly object _lockObj = new();

    public bool TryAcquire(string resourceId, string ownerId, int timeoutMs = 0)
    {
        var entry = new LockEntry
        {
            ResourceId = resourceId,
            OwnerId = ownerId,
            AcquireTime = DateTime.UtcNow
        };

        if (timeoutMs <= 0)
        {
            return _locks.TryAdd(resourceId, entry);
        }

        // 带超时的尝试
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_locks.TryAdd(resourceId, entry))
                return true;
            Thread.Sleep(10);
        }
        return false;
    }

    public void Release(string resourceId, string ownerId)
    {
        if (_locks.TryGetValue(resourceId, out var entry) && entry.OwnerId == ownerId)
        {
            _locks.TryRemove(resourceId, out _);
        }
    }

    public void ReleaseAll(string ownerId)
    {
        foreach (var kvp in _locks)
        {
            if (kvp.Value.OwnerId == ownerId)
            {
                _locks.TryRemove(kvp.Key, out _);
            }
        }
    }

    public bool IsLocked(string resourceId)
    {
        return _locks.ContainsKey(resourceId);
    }

    public string? GetOwner(string resourceId)
    {
        return _locks.TryGetValue(resourceId, out var entry) ? entry.OwnerId : null;
    }

    public void ForceRelease(string resourceId)
    {
        _locks.TryRemove(resourceId, out _);
    }

    public Dictionary<string, string> GetAllLocks()
    {
        return _locks.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OwnerId);
    }

    public IEnumerable<string> GetLocksByOwner(string ownerId)
    {
        return _locks.Values
            .Where(e => e.OwnerId == ownerId)
            .Select(e => e.ResourceId)
            .ToList();
    }

    public int CleanupExpiredLocks(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        var removed = 0;
        foreach (var kvp in _locks)
        {
            if (kvp.Value.AcquireTime < cutoff)
            {
                if (_locks.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }
        return removed;
    }
}

/// <summary>
/// 死锁检测器
/// </summary>
public class DeadlockDetector
{
    private readonly IResourceLockManager _lockManager;
    private readonly TimeSpan _timeout;
    private readonly List<DeadlockCycle> _detectedCycles = new();
    private readonly object _cycleLock = new();

    public DeadlockDetector(IResourceLockManager lockManager, TimeSpan? timeout = null)
    {
        _lockManager = lockManager;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// 执行死锁检测
    /// </summary>
    public List<DeadlockCycle> Detect()
    {
        var allLocks = _lockManager.GetAllLocks();
        var owners = allLocks.Values.Distinct().ToList();

        // 建图: owner -> Set<owner> (who is waiting for whom)
        var waitGraph = new Dictionary<string, HashSet<string>>();

        foreach (var owner in owners)
        {
            waitGraph[owner] = new HashSet<string>();
        }

        // 简单模型: 如果 A 占用 R1, B 想获取 R1, 则 A->B 有边
        // 更精确需要在调用方维护等待关系, 此处简化为全连接检查
        foreach (var kvp in allLocks)
        {
            var resourceOwner = kvp.Value;
            foreach (var waitingOwner in owners)
            {
                if (waitingOwner != resourceOwner)
                {
                    waitGraph[waitingOwner].Add(resourceOwner);
                }
            }
        }

        // DFS 找环
        var cycles = new List<DeadlockCycle>();
        var visited = new HashSet<string>();
        var path = new List<string>();

        foreach (var node in owners)
        {
            if (!visited.Contains(node))
            {
                DetectCycle(node, waitGraph, visited, path, cycles);
            }
        }

        lock (_cycleLock)
        {
            _detectedCycles.Clear();
            _detectedCycles.AddRange(cycles);
        }

        return cycles;
    }

    /// <summary>
    /// 最近检测到的死锁
    /// </summary>
    public IReadOnlyList<DeadlockCycle> GetDetectedCycles()
    {
        lock (_cycleLock)
        {
            return _detectedCycles.ToList();
        }
    }

    /// <summary>
    /// 超时释放所有死锁资源
    /// </summary>
    public int ResolveDeadlocks()
    {
        var cycles = Detect();
        var released = 0;
        foreach (var cycle in cycles)
        {
            // 释放环中第一个 owner 的所有锁
            if (cycle.Owners.Count > 0)
            {
                _lockManager.ReleaseAll(cycle.Owners[0]);
                released++;
            }
        }
        return released;
    }

    private static bool DetectCycle(
        string node,
        Dictionary<string, HashSet<string>> graph,
        HashSet<string> visited,
        List<string> path,
        List<DeadlockCycle> cycles)
    {
        if (path.Contains(node))
        {
            var cycleStart = path.IndexOf(node);
            var cycle = path.Skip(cycleStart).ToList();
            cycles.Add(new DeadlockCycle
            {
                Owners = new List<string>(cycle),
                DetectedAt = DateTime.UtcNow
            });
            return true;
        }

        if (!visited.Add(node))
            return false;

        path.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                DetectCycle(neighbor, graph, visited, path, cycles);
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }
}

/// <summary>
/// 死锁环
/// </summary>
public class DeadlockCycle
{
    public List<string> Owners { get; set; } = new();
    public DateTime DetectedAt { get; set; }

    public override string ToString()
    {
        return $"死锁: {string.Join(" -> ", Owners)} (检测时间: {DetectedAt:HH:mm:ss})";
    }
}
