namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public interface ITransportDeadlockService
{
    IReadOnlyList<TransportDeadlockCycle> Detect();
    TransportDeadlockResolution Resolve(string cycleId);
    IReadOnlyList<TransportDeadlockCycle> GetLastDetectedCycles();
}

/// <summary>
/// 交通死锁处置：暂停低优先级受害任务、撤销其等待请求并释放未被确认物理占用的未来资源。
/// 不会强制释放已确认被车辆占用的单轨区或交叉口。
/// </summary>
public sealed class TransportDeadlockService : ITransportDeadlockService
{
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly ITransportExecutionEngine? _execution;
    private readonly IRouteReservationManager? _reservations;
    private readonly ConcurrentDictionary<string, TransportDeadlockCycle> _lastCycles = new(StringComparer.Ordinal);

    public TransportDeadlockService(
        ITransportTrafficCoordinator traffic,
        ITransportExecutionEngine? execution = null,
        IRouteReservationManager? reservations = null)
    {
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _execution = execution;
        _reservations = reservations;
    }

    public IReadOnlyList<TransportDeadlockCycle> Detect()
    {
        var cycles = _traffic.DetectDeadlocks();
        _lastCycles.Clear();
        foreach (var cycle in cycles)
            _lastCycles[cycle.CycleId] = cycle;
        return cycles;
    }

    public IReadOnlyList<TransportDeadlockCycle> GetLastDetectedCycles() =>
        _lastCycles.Values.OrderBy(x => x.CycleId, StringComparer.Ordinal).ToArray();

    public TransportDeadlockResolution Resolve(string cycleId)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
            return NotFound(cycleId);

        if (!_lastCycles.TryGetValue(cycleId, out var cycle))
            cycle = Detect().FirstOrDefault(x => string.Equals(x.CycleId, cycleId, StringComparison.Ordinal));
        if (cycle is null)
            return NotFound(cycleId);

        var requests = _traffic.GetRequests().ToDictionary(x => x.OwnerId, StringComparer.Ordinal);
        var victim = cycle.OwnerIds
            .Select(ownerId => requests.GetValueOrDefault(ownerId) ?? new TransportTrafficRequestInfo
            {
                OwnerId = ownerId,
                VehicleId = ownerId,
                Priority = 0
            })
            .OrderBy(x => x.Priority)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.OwnerId, StringComparer.Ordinal)
            .First();

        var protectedResources = Array.Empty<string>();
        TransportExecutionSnapshot? executionSnapshot = null;
        if (_execution is not null && _execution.TryGet(victim.OwnerId, out executionSnapshot) && executionSnapshot is not null)
        {
            if (!executionSnapshot.IsTerminal)
                _execution.Pause(victim.OwnerId);

            if (_reservations is not null && executionSnapshot.ActiveReservedEdges.Count > 0)
            {
                // 运动状态下保留最靠近车辆的第一条边作为安全缓冲，只释放更远的未来窗口。
                var preserveFirst = executionSnapshot.State is
                    TransportExecutionState.MovingToPickup or
                    TransportExecutionState.MovingToDestination;
                if (preserveFirst)
                {
                    protectedResources = _traffic
                        .GetResourceIdsForEdges(executionSnapshot.ActiveReservedEdges.Take(1))
                        .ToArray();
                }

                var releasableEdges = executionSnapshot.ActiveReservedEdges
                    .Skip(preserveFirst ? 1 : 0)
                    .ToArray();
                if (releasableEdges.Length > 0)
                    _reservations.ReleaseEdges(executionSnapshot.ReservationId, releasableEdges);
            }
        }

        _traffic.CancelWait(victim.OwnerId);
        var released = _traffic.ReleaseUnoccupiedResources(victim.OwnerId, protectedResources);
        var retained = _traffic.GetHolds()
            .Where(x => string.Equals(x.OwnerId, victim.OwnerId, StringComparison.Ordinal) &&
                        (x.OccupancyConfirmed || protectedResources.Contains(x.ResourceId, StringComparer.Ordinal)))
            .Select(x => x.ResourceId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var stillExists = _traffic.DetectDeadlocks()
            .Any(x => x.OwnerIds.Intersect(cycle.OwnerIds, StringComparer.Ordinal).Count() > 1);

        var status = stillExists
            ? TransportDeadlockResolutionStatus.RequiresManualIntervention
            : retained.Length > 0
                ? TransportDeadlockResolutionStatus.CycleBrokenAwaitingClearance
                : TransportDeadlockResolutionStatus.Resolved;

        var message = status switch
        {
            TransportDeadlockResolutionStatus.Resolved => "已暂停受害任务并释放未来交通资源，死锁环已解除",
            TransportDeadlockResolutionStatus.CycleBrokenAwaitingClearance => "死锁环已打断，但车辆仍占用物理资源或安全缓冲区，等待其安全退出",
            _ => "自动处置后仍存在循环等待，需要人工介入"
        };

        var resolution = new TransportDeadlockResolution
        {
            CycleId = cycle.CycleId,
            VictimOwnerId = victim.OwnerId,
            Status = status,
            ReleasedResourceIds = released,
            RetainedOccupiedResourceIds = retained,
            Message = message
        };

        _traffic.RecordIncident(new TransportTrafficIncident
        {
            IncidentType = "DeadlockResolution",
            OwnerId = victim.OwnerId,
            Message = $"{message}；Cycle={cycle.CycleId}"
        });

        _lastCycles.TryRemove(cycle.CycleId, out _);
        return resolution;
    }

    private static TransportDeadlockResolution NotFound(string cycleId) => new()
    {
        CycleId = cycleId ?? string.Empty,
        Status = TransportDeadlockResolutionStatus.CycleNotFound,
        Message = "未找到指定死锁环，可能已自行解除"
    };
}
