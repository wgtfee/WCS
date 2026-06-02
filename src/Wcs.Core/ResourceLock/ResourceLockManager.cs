namespace Wcs.Core.ResourceLock;

using System.Collections.Concurrent;

/// <summary>
/// 资源锁管理器接口 - 设备互斥锁
/// </summary>
public interface IResourceLockManager
{
    /// <summary>
    /// 尝试获取资源锁（同步，兼容旧接口）
    /// </summary>
    bool TryAcquire(string resourceId, string ownerId, int timeoutMs = 0);

    /// <summary>
    /// 异步尝试获取资源锁（支持 TTL/Lease）
    /// </summary>
    Task<LockAcquireResult> TryAcquireAsync(string resourceId, string ownerId,
        TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// 释放资源锁
    /// </summary>
    void Release(string resourceId, string ownerId);

    /// <summary>
    /// 释放某拥有者的所有锁
    /// </summary>
    void ReleaseAll(string ownerId);

    /// <summary>
    /// 续约锁 — 延长锁的过期时间
    /// </summary>
    bool RenewLease(string resourceId, string ownerId, string leaseToken, TimeSpan extension);

    /// <summary>
    /// 查询资源是否被锁定
    /// </summary>
    bool IsLocked(string resourceId);

    /// <summary>
    /// 获取锁的持有者
    /// </summary>
    string? GetOwner(string resourceId);

    /// <summary>
    /// 获取锁的剩余生存时间
    /// </summary>
    TimeSpan? GetRemainingTtl(string resourceId);

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
    /// 手动清理过期锁
    /// </summary>
    int CleanupExpiredLocks(TimeSpan maxAge);
}

/// <summary>
/// 锁获取结果
/// </summary>
public class LockAcquireResult
{
    public bool Success { get; set; }
    public string? LeaseToken { get; set; }
    public string? OwnerId { get; set; }
    public DateTime? ExpiryTime { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>
/// 资源锁条目 — 含 TTL/Lease 支持
/// </summary>
internal class LockEntry
{
    public string ResourceId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public DateTime AcquireTime { get; set; } = DateTime.UtcNow;
    public TimeSpan? Ttl { get; set; }
    public DateTime? ExpiryTime { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public string? LeaseToken { get; set; }
}

/// <summary>
/// 资源锁管理器实现 - 基于 ConcurrentDictionary
/// 增强：TTL/Lease、异步获取、心跳续约、后台自动清理
/// </summary>
public class ResourceLockManager : IResourceLockManager, IDisposable
{
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(5);
    private bool _disposed;

    public ResourceLockManager()
    {
        _cleanupTimer = new Timer(_ => AutoCleanup(), null,
            _cleanupInterval, _cleanupInterval);
    }

    // ==================== 同步获取（兼容旧接口） ====================

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
            // 先检查是否有未过期的锁
            if (_locks.TryGetValue(resourceId, out var existing))
            {
                if (existing.ExpiryTime.HasValue && existing.ExpiryTime < DateTime.UtcNow)
                {
                    // 锁已过期，尝试替换
                    var replaced = _locks.TryUpdate(resourceId, entry, existing);
                    if (replaced) return true;
                }
                return _locks.TryAdd(resourceId, entry);
            }
            return _locks.TryAdd(resourceId, entry);
        }

        // 带超时的同步等待（兼容旧行为）
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_locks.TryGetValue(resourceId, out var existing))
            {
                if (existing.ExpiryTime.HasValue && existing.ExpiryTime < DateTime.UtcNow)
                {
                    if (_locks.TryUpdate(resourceId, entry, existing))
                        return true;
                }
            }
            else if (_locks.TryAdd(resourceId, entry))
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return false;
    }

    // ==================== 异步获取（推荐） ====================

    public async Task<LockAcquireResult> TryAcquireAsync(string resourceId, string ownerId,
        TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var leaseToken = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var entry = new LockEntry
        {
            ResourceId = resourceId,
            OwnerId = ownerId,
            AcquireTime = now,
            Ttl = ttl,
            ExpiryTime = ttl.HasValue ? now.Add(ttl.Value) : null,
            LeaseToken = leaseToken,
            LastHeartbeat = now
        };

        // 尝试直接添加
        if (_locks.TryAdd(resourceId, entry))
        {
            return new LockAcquireResult
            {
                Success = true,
                LeaseToken = leaseToken,
                OwnerId = ownerId,
                ExpiryTime = entry.ExpiryTime
            };
        }

        // 检查现有锁是否已过期
        if (_locks.TryGetValue(resourceId, out var existing) &&
            existing.ExpiryTime.HasValue &&
            existing.ExpiryTime < DateTime.UtcNow)
        {
            var replaced = _locks.TryUpdate(resourceId, entry, existing);
            if (replaced)
            {
                return new LockAcquireResult
                {
                    Success = true,
                    LeaseToken = leaseToken,
                    OwnerId = ownerId,
                    ExpiryTime = entry.ExpiryTime
                };
            }
        }

        // 获取当前锁的剩余时间用于 FailureReason
        var currentOwner = GetOwner(resourceId);
        var remaining = GetRemainingTtl(resourceId);

        return new LockAcquireResult
        {
            Success = false,
            FailureReason = $"Resource '{resourceId}' already locked by '{currentOwner}'",
            OwnerId = currentOwner,
            ExpiryTime = existing?.ExpiryTime
        };
    }

    // ==================== 释放 ====================

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

    // ==================== 续约 ====================

    public bool RenewLease(string resourceId, string ownerId, string leaseToken, TimeSpan extension)
    {
        if (!_locks.TryGetValue(resourceId, out var entry))
            return false;

        if (entry.OwnerId != ownerId || entry.LeaseToken != leaseToken)
            return false;

        lock (entry)
        {
            if (entry.LeaseToken != leaseToken) // double-check after lock
                return false;

            entry.ExpiryTime = DateTime.UtcNow.Add(extension);
            entry.LastHeartbeat = DateTime.UtcNow;
            return true;
        }
    }

    // ==================== 查询 ====================

    public bool IsLocked(string resourceId)
    {
        if (!_locks.TryGetValue(resourceId, out var entry))
            return false;

        // 检查锁是否已过期
        if (entry.ExpiryTime.HasValue && entry.ExpiryTime < DateTime.UtcNow)
            return false;

        return true;
    }

    public string? GetOwner(string resourceId)
    {
        if (!_locks.TryGetValue(resourceId, out var entry))
            return null;

        if (entry.ExpiryTime.HasValue && entry.ExpiryTime < DateTime.UtcNow)
            return null;

        return entry.OwnerId;
    }

    public TimeSpan? GetRemainingTtl(string resourceId)
    {
        if (!_locks.TryGetValue(resourceId, out var entry))
            return null;

        if (!entry.ExpiryTime.HasValue)
            return null; // 无限期

        var remaining = entry.ExpiryTime.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void ForceRelease(string resourceId)
    {
        _locks.TryRemove(resourceId, out _);
    }

    public Dictionary<string, string> GetAllLocks()
    {
        var now = DateTime.UtcNow;
        return _locks
            .Where(kvp => !kvp.Value.ExpiryTime.HasValue || kvp.Value.ExpiryTime > now)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OwnerId);
    }

    public IEnumerable<string> GetLocksByOwner(string ownerId)
    {
        var now = DateTime.UtcNow;
        return _locks.Values
            .Where(e => e.OwnerId == ownerId && (!e.ExpiryTime.HasValue || e.ExpiryTime > now))
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

    // ==================== 后台自动清理 ====================

    private void AutoCleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _locks)
        {
            // 只清理有 TTL 且已过期的锁
            if (kvp.Value.ExpiryTime.HasValue && kvp.Value.ExpiryTime < now)
            {
                _locks.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cleanupTimer.Dispose();
        }
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

        var waitGraph = new Dictionary<string, HashSet<string>>();
        foreach (var owner in owners)
            waitGraph[owner] = new HashSet<string>();

        foreach (var kvp in allLocks)
        {
            var resourceOwner = kvp.Value;
            foreach (var waitingOwner in owners)
            {
                if (waitingOwner != resourceOwner)
                    waitGraph[waitingOwner].Add(resourceOwner);
            }
        }

        var cycles = new List<DeadlockCycle>();
        var visited = new HashSet<string>();
        var path = new List<string>();

        foreach (var node in owners)
        {
            if (!visited.Contains(node))
                DetectCycle(node, waitGraph, visited, path, cycles);
        }

        lock (_cycleLock)
        {
            _detectedCycles.Clear();
            _detectedCycles.AddRange(cycles);
        }

        return cycles;
    }

    public IReadOnlyList<DeadlockCycle> GetDetectedCycles()
    {
        lock (_cycleLock)
        {
            return _detectedCycles.ToList();
        }
    }

    public int ResolveDeadlocks()
    {
        var cycles = Detect();
        var released = 0;
        foreach (var cycle in cycles)
        {
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
                DetectCycle(neighbor, graph, visited, path, cycles);
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
        return $"Deadlock: {string.Join(" -> ", Owners)} (detected: {DetectedAt:HH:mm:ss})";
    }
}
