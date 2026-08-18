namespace Wcs.Core.TransportScheduling;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wcs.Core.Common.Interfaces;

public interface ITransportTrafficCoordinator
{
    bool RegisterResource(TransportTrafficResourceDefinition definition);
    bool RemoveResource(string resourceId);
    void RegisterRequest(string ownerId, string vehicleId, int priority);
    TransportTrafficAcquireResult TryAcquire(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        DateTime? nowUtc = null);
    TransportTrafficAcquireResult SynchronizeOwnerEdges(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        DateTime? nowUtc = null);
    IReadOnlyList<string> ReleaseOwner(string ownerId, bool includeOccupied = true);
    IReadOnlyList<string> ReleaseUnoccupiedResources(
        string ownerId,
        IReadOnlyCollection<string>? protectedResourceIds = null);
    bool CancelWait(string ownerId);
    bool MarkOccupancy(string ownerId, string resourceId, bool occupied);
    IReadOnlyList<string> GetResourceIdsForEdges(IEnumerable<string> edgeIds);
    IReadOnlyList<TransportTrafficResourceDefinition> GetResources();
    IReadOnlyList<TransportTrafficRequestInfo> GetRequests();
    IReadOnlyList<TransportTrafficHold> GetHolds();
    IReadOnlyList<TransportTrafficWait> GetWaits();
    IReadOnlyList<TransportTrafficIncident> GetIncidents();
    IReadOnlyList<TransportDeadlockCycle> DetectDeadlocks(DateTime? nowUtc = null);
    void RecordIncident(TransportTrafficIncident incident);
    TransportTrafficSnapshot GetSnapshot();
}

/// <summary>
/// EMS/RGV 第四阶段交通控制中心。
/// 只基于真实等待关系建立 Wait-For Graph，避免“所有锁持有者互相等待”的假死锁。
/// </summary>
public sealed class TransportTrafficCoordinator : ITransportTrafficCoordinator, ISnapshotProvider
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TransportTrafficResourceDefinition> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _edgeToResources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransportTrafficRequestInfo> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TransportTrafficHold>> _holdsByResource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransportTrafficWait> _waitsByOwner = new(StringComparer.Ordinal);
    private readonly List<TransportTrafficIncident> _incidents = new();

    public string ModuleName => "TransportTraffic";
    public int RestoreOrder => 4;

    public bool RegisterResource(TransportTrafficResourceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);

        lock (_sync)
        {
            if (_resources.TryGetValue(definition.ResourceId, out var old))
                RemoveEdgeMappingsUnsafe(old);

            _resources[definition.ResourceId] = definition with
            {
                EdgeIds = Normalize(definition.EdgeIds)
            };
            AddEdgeMappingsUnsafe(_resources[definition.ResourceId]);
            _holdsByResource.TryAdd(definition.ResourceId, new Dictionary<string, TransportTrafficHold>(StringComparer.Ordinal));
            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
            return true;
        }
    }

    public bool RemoveResource(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return false;

        lock (_sync)
        {
            if (!_resources.TryGetValue(resourceId, out var definition))
                return false;

            if (_holdsByResource.TryGetValue(resourceId, out var holds) &&
                holds.Values.Any(x => x.OccupancyConfirmed))
            {
                return false;
            }

            RemoveEdgeMappingsUnsafe(definition);
            _resources.Remove(resourceId);
            _holdsByResource.Remove(resourceId);
            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
            return true;
        }
    }

    public void RegisterRequest(string ownerId, string vehicleId, int priority)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("ownerId 不能为空", nameof(ownerId));

        lock (_sync)
        {
            if (_requests.TryGetValue(ownerId, out var existing))
            {
                _requests[ownerId] = existing with
                {
                    VehicleId = string.IsNullOrWhiteSpace(vehicleId) ? existing.VehicleId : vehicleId,
                    Priority = priority
                };
                return;
            }

            _requests[ownerId] = new TransportTrafficRequestInfo
            {
                OwnerId = ownerId,
                VehicleId = vehicleId,
                Priority = priority
            };
        }
    }

    public TransportTrafficAcquireResult TryAcquire(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("ownerId 不能为空", nameof(ownerId));
        ArgumentNullException.ThrowIfNull(edgeIds);
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease));

        lock (_sync)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            CleanupExpiredUnsafe(now);
            EnsureRequestUnsafe(ownerId);

            var resourceIds = GetResourceIdsForEdgesUnsafe(edgeIds);
            if (resourceIds.Count == 0)
            {
                _waitsByOwner.Remove(ownerId);
                return TransportTrafficAcquireResult.Granted(Array.Empty<string>());
            }

            var blockers = GetBlockingOwnersUnsafe(ownerId, resourceIds);
            var queueBlocker = blockers.Count == 0 && !IsQueueHeadUnsafe(ownerId, resourceIds, now);

            if (blockers.Count > 0 || queueBlocker)
            {
                var request = _requests[ownerId];
                var existingWait = _waitsByOwner.GetValueOrDefault(ownerId);
                var reason = blockers.Count > 0
                    ? $"交通资源被占用：{string.Join(", ", blockers)}"
                    : "存在优先级更高或等待更久的任务";

                _waitsByOwner[ownerId] = new TransportTrafficWait
                {
                    WaitId = existingWait?.WaitId ?? Guid.NewGuid().ToString("N"),
                    OwnerId = ownerId,
                    VehicleId = request.VehicleId,
                    RequestedResourceIds = resourceIds,
                    BlockingOwnerIds = blockers,
                    Priority = request.Priority,
                    WaitingSinceUtc = existingWait?.WaitingSinceUtc ?? now,
                    Reason = reason
                };

                return TransportTrafficAcquireResult.Denied(resourceIds, blockers, reason);
            }

            foreach (var resourceId in resourceIds)
            {
                var resourceHolds = _holdsByResource[resourceId];
                if (resourceHolds.TryGetValue(ownerId, out var current))
                {
                    resourceHolds[ownerId] = current with { ExpiresAtUtc = now.Add(lease) };
                    continue;
                }

                var request = _requests[ownerId];
                resourceHolds[ownerId] = new TransportTrafficHold
                {
                    ResourceId = resourceId,
                    OwnerId = ownerId,
                    VehicleId = request.VehicleId,
                    AcquiredAtUtc = now,
                    ExpiresAtUtc = now.Add(lease)
                };
            }

            _waitsByOwner.Remove(ownerId);
            RefreshWaitBlockersUnsafe(now);
            return TransportTrafficAcquireResult.Granted(resourceIds);
        }
    }

    public TransportTrafficAcquireResult SynchronizeOwnerEdges(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        DateTime? nowUtc = null)
    {
        var result = TryAcquire(ownerId, edgeIds, lease, nowUtc);
        if (!result.Success)
            return result;

        lock (_sync)
        {
            var desired = GetResourceIdsForEdgesUnsafe(edgeIds).ToHashSet(StringComparer.Ordinal);
            foreach (var (resourceId, holds) in _holdsByResource)
            {
                if (!desired.Contains(resourceId))
                    holds.Remove(ownerId);
            }
            RefreshWaitBlockersUnsafe(nowUtc ?? DateTime.UtcNow);
            return result;
        }
    }

    public IReadOnlyList<string> ReleaseOwner(string ownerId, bool includeOccupied = true)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return Array.Empty<string>();

        lock (_sync)
        {
            var released = new List<string>();
            foreach (var (resourceId, holds) in _holdsByResource)
            {
                if (!holds.TryGetValue(ownerId, out var hold))
                    continue;
                if (!includeOccupied && hold.OccupancyConfirmed)
                    continue;

                holds.Remove(ownerId);
                released.Add(resourceId);
            }

            _waitsByOwner.Remove(ownerId);
            if (includeOccupied)
                _requests.Remove(ownerId);
            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
            return released;
        }
    }

    public IReadOnlyList<string> ReleaseUnoccupiedResources(
        string ownerId,
        IReadOnlyCollection<string>? protectedResourceIds = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return Array.Empty<string>();

        lock (_sync)
        {
            var protectedSet = (protectedResourceIds ?? Array.Empty<string>())
                .ToHashSet(StringComparer.Ordinal);
            var released = new List<string>();
            foreach (var (resourceId, holds) in _holdsByResource)
            {
                if (protectedSet.Contains(resourceId) ||
                    !holds.TryGetValue(ownerId, out var hold) ||
                    hold.OccupancyConfirmed)
                {
                    continue;
                }

                holds.Remove(ownerId);
                released.Add(resourceId);
            }

            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
            return released;
        }
    }

    public bool CancelWait(string ownerId)
    {
        lock (_sync)
        {
            var removed = _waitsByOwner.Remove(ownerId);
            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
            return removed;
        }
    }

    public bool MarkOccupancy(string ownerId, string resourceId, bool occupied)
    {
        lock (_sync)
        {
            if (!_holdsByResource.TryGetValue(resourceId, out var holds) ||
                !holds.TryGetValue(ownerId, out var hold))
            {
                return false;
            }

            holds[ownerId] = hold with
            {
                OccupancyConfirmed = occupied,
                ExpiresAtUtc = occupied ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(30)
            };
            return true;
        }
    }

    public IReadOnlyList<string> GetResourceIdsForEdges(IEnumerable<string> edgeIds)
    {
        ArgumentNullException.ThrowIfNull(edgeIds);
        lock (_sync)
            return GetResourceIdsForEdgesUnsafe(edgeIds);
    }

    public IReadOnlyList<TransportTrafficResourceDefinition> GetResources()
    {
        lock (_sync)
            return _resources.Values.OrderBy(x => x.ResourceId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<TransportTrafficRequestInfo> GetRequests()
    {
        lock (_sync)
            return _requests.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAtUtc).ToArray();
    }

    public IReadOnlyList<TransportTrafficHold> GetHolds()
    {
        lock (_sync)
        {
            CleanupExpiredUnsafe(DateTime.UtcNow);
            return _holdsByResource.Values
                .SelectMany(x => x.Values)
                .OrderBy(x => x.ResourceId, StringComparer.Ordinal)
                .ThenBy(x => x.OwnerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<TransportTrafficWait> GetWaits()
    {
        lock (_sync)
        {
            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
            return _waitsByOwner.Values
                .OrderByDescending(x => EffectivePriorityUnsafe(x, DateTime.UtcNow))
                .ThenBy(x => x.WaitingSinceUtc)
                .ToArray();
        }
    }

    public IReadOnlyList<TransportTrafficIncident> GetIncidents()
    {
        lock (_sync)
            return _incidents.OrderByDescending(x => x.OccurredAtUtc).ToArray();
    }

    public IReadOnlyList<TransportDeadlockCycle> DetectDeadlocks(DateTime? nowUtc = null)
    {
        lock (_sync)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            RefreshWaitBlockersUnsafe(now);

            var waits = _waitsByOwner.Values.ToDictionary(x => x.OwnerId, StringComparer.Ordinal);
            var graph = waits.ToDictionary(
                x => x.Key,
                x => x.Value.BlockingOwnerIds.Where(waits.ContainsKey).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new List<string>();
            var cycles = new Dictionary<string, TransportDeadlockCycle>(StringComparer.Ordinal);

            foreach (var owner in graph.Keys)
            {
                if (!state.TryGetValue(owner, out var value) || value == 0)
                    DetectCyclesDepthFirstUnsafe(owner, graph, waits, state, stack, cycles, now);
            }

            return cycles.Values.OrderBy(x => x.CycleId, StringComparer.Ordinal).ToArray();
        }
    }

    public void RecordIncident(TransportTrafficIncident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        lock (_sync)
        {
            _incidents.Add(incident);
            if (_incidents.Count > 1000)
                _incidents.RemoveRange(0, _incidents.Count - 1000);
        }
    }

    public TransportTrafficSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            CleanupExpiredUnsafe(DateTime.UtcNow);
            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
            return new TransportTrafficSnapshot
            {
                Resources = _resources.Values.OrderBy(x => x.ResourceId, StringComparer.Ordinal).ToArray(),
                Requests = _requests.Values.OrderBy(x => x.OwnerId, StringComparer.Ordinal).ToArray(),
                Holds = _holdsByResource.Values.SelectMany(x => x.Values).ToArray(),
                Waits = _waitsByOwner.Values.ToArray(),
                Incidents = _incidents.ToArray()
            };
        }
    }

    public Task<object> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<object>(GetSnapshot());
    }

    public Task RestoreSnapshotAsync(object snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var restored = snapshot switch
        {
            TransportTrafficSnapshot typed => typed,
            JsonElement element => element.Deserialize<TransportTrafficSnapshot>(),
            _ => JsonSerializer.Deserialize<TransportTrafficSnapshot>(JsonSerializer.Serialize(snapshot))
        } ?? throw new InvalidOperationException("交通控制快照格式无效");

        lock (_sync)
        {
            _resources.Clear();
            _edgeToResources.Clear();
            _requests.Clear();
            _holdsByResource.Clear();
            _waitsByOwner.Clear();
            _incidents.Clear();

            foreach (var resource in restored.Resources)
            {
                ValidateDefinition(resource);
                _resources[resource.ResourceId] = resource;
                AddEdgeMappingsUnsafe(resource);
                _holdsByResource[resource.ResourceId] = new Dictionary<string, TransportTrafficHold>(StringComparer.Ordinal);
            }
            foreach (var request in restored.Requests)
                _requests[request.OwnerId] = request;
            foreach (var hold in restored.Holds)
            {
                if (_holdsByResource.TryGetValue(hold.ResourceId, out var holds))
                    holds[hold.OwnerId] = hold;
            }
            foreach (var wait in restored.Waits)
                _waitsByOwner[wait.OwnerId] = wait;
            _incidents.AddRange(restored.Incidents);
            RefreshWaitBlockersUnsafe(DateTime.UtcNow);
        }

        return Task.CompletedTask;
    }

    private void EnsureRequestUnsafe(string ownerId)
    {
        if (!_requests.ContainsKey(ownerId))
        {
            _requests[ownerId] = new TransportTrafficRequestInfo
            {
                OwnerId = ownerId,
                VehicleId = ownerId,
                Priority = 0
            };
        }
    }

    private List<string> GetBlockingOwnersUnsafe(string ownerId, IReadOnlyList<string> resourceIds)
    {
        var blockers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resourceId in resourceIds)
        {
            if (!_resources.TryGetValue(resourceId, out var definition) ||
                !_holdsByResource.TryGetValue(resourceId, out var resourceHolds))
            {
                continue;
            }

            var otherHolds = resourceHolds.Values
                .Where(x => !string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal))
                .ToArray();

            if (otherHolds.Length >= definition.Capacity)
            {
                foreach (var hold in otherHolds)
                    blockers.Add(hold.OwnerId);
            }
        }
        return blockers.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private bool IsQueueHeadUnsafe(string ownerId, IReadOnlyList<string> resourceIds, DateTime now)
    {
        foreach (var resourceId in resourceIds)
        {
            var candidates = _waitsByOwner.Values
                .Where(x => x.RequestedResourceIds.Contains(resourceId, StringComparer.Ordinal))
                .Where(x => !string.Equals(x.OwnerId, ownerId, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
                continue;

            var request = _requests[ownerId];
            var current = _waitsByOwner.GetValueOrDefault(ownerId) ?? new TransportTrafficWait
            {
                OwnerId = ownerId,
                VehicleId = request.VehicleId,
                Priority = request.Priority,
                WaitingSinceUtc = now,
                RequestedResourceIds = resourceIds
            };
            candidates.Add(current);

            var winner = candidates
                .OrderByDescending(x => EffectivePriorityUnsafe(x, now))
                .ThenBy(x => x.WaitingSinceUtc)
                .ThenBy(x => x.OwnerId, StringComparer.Ordinal)
                .First();

            if (!string.Equals(winner.OwnerId, ownerId, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private int EffectivePriorityUnsafe(TransportTrafficWait wait, DateTime now)
    {
        var interval = wait.RequestedResourceIds
            .Where(_resources.ContainsKey)
            .Select(x => Math.Max(1, _resources[x].AgingIntervalSeconds))
            .DefaultIfEmpty(30)
            .Min();
        var ageBoost = Math.Max(0, (int)((now - wait.WaitingSinceUtc).TotalSeconds / interval));
        return wait.Priority + ageBoost;
    }

    private List<string> GetResourceIdsForEdgesUnsafe(IEnumerable<string> edgeIds)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edgeId in edgeIds.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (!_edgeToResources.TryGetValue(edgeId, out var resources))
                continue;
            foreach (var resourceId in resources)
            {
                if (_resources.TryGetValue(resourceId, out var definition) && definition.Enabled)
                    result.Add(resourceId);
            }
        }
        return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private void CleanupExpiredUnsafe(DateTime now)
    {
        foreach (var holds in _holdsByResource.Values)
        {
            foreach (var ownerId in holds.Values
                         .Where(x => !x.OccupancyConfirmed && x.ExpiresAtUtc <= now)
                         .Select(x => x.OwnerId)
                         .ToArray())
            {
                holds.Remove(ownerId);
            }
        }
        RefreshWaitBlockersUnsafe(now);
    }

    private void RefreshWaitBlockersUnsafe(DateTime now)
    {
        foreach (var ownerId in _waitsByOwner.Keys.ToArray())
        {
            var wait = _waitsByOwner[ownerId];
            var blockers = GetBlockingOwnersUnsafe(ownerId, wait.RequestedResourceIds);
            _waitsByOwner[ownerId] = wait with
            {
                BlockingOwnerIds = blockers,
                Reason = blockers.Count > 0
                    ? $"交通资源被占用：{string.Join(", ", blockers)}"
                    : "等待调度器按优先级和先到先服务重新尝试"
            };
        }
    }

    private static void DetectCyclesDepthFirstUnsafe(
        string owner,
        IReadOnlyDictionary<string, string[]> graph,
        IReadOnlyDictionary<string, TransportTrafficWait> waits,
        IDictionary<string, int> state,
        IList<string> stack,
        IDictionary<string, TransportDeadlockCycle> cycles,
        DateTime now)
    {
        state[owner] = 1;
        stack.Add(owner);

        if (graph.TryGetValue(owner, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                state.TryGetValue(neighbor, out var neighborState);
                if (neighborState == 0)
                {
                    DetectCyclesDepthFirstUnsafe(neighbor, graph, waits, state, stack, cycles, now);
                }
                else if (neighborState == 1)
                {
                    var index = stack.IndexOf(neighbor);
                    if (index < 0)
                        continue;

                    var owners = stack.Skip(index).ToArray();
                    var canonicalOwners = CanonicalizeCycle(owners);
                    var key = string.Join("|", canonicalOwners);
                    if (cycles.ContainsKey(key))
                        continue;

                    var resourceIds = canonicalOwners
                        .Where(waits.ContainsKey)
                        .SelectMany(x => waits[x].RequestedResourceIds)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray();

                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
                    cycles[key] = new TransportDeadlockCycle
                    {
                        CycleId = $"TDL-{hash}",
                        OwnerIds = canonicalOwners,
                        ResourceIds = resourceIds,
                        DetectedAtUtc = now
                    };
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        state[owner] = 2;
    }

    private static string[] CanonicalizeCycle(IReadOnlyList<string> owners)
    {
        if (owners.Count <= 1)
            return owners.ToArray();

        var rotations = Enumerable.Range(0, owners.Count)
            .Select(index => owners.Skip(index).Concat(owners.Take(index)).ToArray())
            .ToArray();
        return rotations.OrderBy(x => string.Join("|", x), StringComparer.Ordinal).First();
    }

    private void AddEdgeMappingsUnsafe(TransportTrafficResourceDefinition definition)
    {
        foreach (var edgeId in definition.EdgeIds)
        {
            if (!_edgeToResources.TryGetValue(edgeId, out var resources))
            {
                resources = new HashSet<string>(StringComparer.Ordinal);
                _edgeToResources[edgeId] = resources;
            }
            resources.Add(definition.ResourceId);
        }
    }

    private void RemoveEdgeMappingsUnsafe(TransportTrafficResourceDefinition definition)
    {
        foreach (var edgeId in definition.EdgeIds)
        {
            if (!_edgeToResources.TryGetValue(edgeId, out var resources))
                continue;
            resources.Remove(definition.ResourceId);
            if (resources.Count == 0)
                _edgeToResources.Remove(edgeId);
        }
    }

    private static string[] Normalize(IEnumerable<string> values) =>
        values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();

    private static void ValidateDefinition(TransportTrafficResourceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ResourceId))
            throw new ArgumentException("ResourceId 不能为空", nameof(definition));
        if (definition.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(definition), "Capacity 必须大于 0");
        if (definition.AgingIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(definition), "AgingIntervalSeconds 必须大于 0");
        if (definition.EdgeIds.Count == 0)
            throw new ArgumentException("交通资源至少需要关联一个路段", nameof(definition));
    }
}
