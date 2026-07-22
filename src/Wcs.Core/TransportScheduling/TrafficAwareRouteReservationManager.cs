namespace Wcs.Core.TransportScheduling;

/// <summary>
/// 在原子路段预留之前增加交叉口、单轨区和合流点的交通资源门禁。
/// 路段预留失败时会回滚本次新增的交通资源占用。
/// </summary>
public sealed class TrafficAwareRouteReservationManager : IRouteReservationManager
{
    private readonly InMemoryRouteReservationManager _inner;
    private readonly ITransportTrafficCoordinator _traffic;

    public TrafficAwareRouteReservationManager(
        InMemoryRouteReservationManager inner,
        ITransportTrafficCoordinator traffic)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
    }

    public bool TryReserve(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        out RouteReservation? reservation)
    {
        var trafficResult = _traffic.TryAcquire(ownerId, edgeIds, lease);
        if (!trafficResult.Success)
        {
            reservation = null;
            return false;
        }

        if (_inner.TryReserve(ownerId, edgeIds, lease, out reservation) && reservation is not null)
        {
            _traffic.SynchronizeOwnerEdges(ownerId, reservation.EdgeIds, lease);
            return true;
        }

        _traffic.ReleaseOwner(ownerId, includeOccupied: false);
        return false;
    }

    public bool TryExtend(
        string reservationId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        out RouteReservation? reservation)
    {
        if (!_inner.TryGet(reservationId, out var current) || current is null)
        {
            reservation = null;
            return false;
        }

        var trafficResult = _traffic.TryAcquire(current.OwnerId, edgeIds, lease);
        if (!trafficResult.Success)
        {
            reservation = current;
            return false;
        }

        if (_inner.TryExtend(reservationId, edgeIds, lease, out reservation) && reservation is not null)
        {
            _traffic.SynchronizeOwnerEdges(current.OwnerId, reservation.EdgeIds, lease);
            return true;
        }

        _traffic.SynchronizeOwnerEdges(current.OwnerId, current.EdgeIds, lease);
        reservation = current;
        return false;
    }

    public bool ReleaseEdges(string reservationId, IReadOnlyCollection<string> edgeIds)
    {
        if (!_inner.TryGet(reservationId, out var current) || current is null)
            return false;

        var released = _inner.ReleaseEdges(reservationId, edgeIds);
        if (!released)
            return false;

        if (_inner.TryGet(reservationId, out var remaining) && remaining is not null)
        {
            var remainingLease = remaining.ExpiresAtUtc - DateTime.UtcNow;
            _traffic.SynchronizeOwnerEdges(
                current.OwnerId,
                remaining.EdgeIds,
                remainingLease > TimeSpan.Zero ? remainingLease : TimeSpan.FromSeconds(1));
        }
        else
            _traffic.ReleaseOwner(current.OwnerId);

        return true;
    }

    public bool Renew(string reservationId, TimeSpan lease) => _inner.Renew(reservationId, lease);

    public bool TryGet(string reservationId, out RouteReservation? reservation) =>
        _inner.TryGet(reservationId, out reservation);

    public bool Release(string reservationId)
    {
        _inner.TryGet(reservationId, out var current);
        var released = _inner.Release(reservationId);
        if (released && current is not null)
            _traffic.ReleaseOwner(current.OwnerId);
        return released;
    }

    public int CleanupExpired(DateTime? nowUtc = null)
    {
        var before = _inner.GetActiveReservations();
        var removed = _inner.CleanupExpired(nowUtc);
        if (removed <= 0)
            return removed;

        var activeIds = _inner.GetActiveReservations()
            .Select(x => x.ReservationId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var expired in before.Where(x => !activeIds.Contains(x.ReservationId)))
        {
            // 已确认处于物理占用状态的交通资源不能仅因租约过期而自动释放。
            _traffic.ReleaseOwner(expired.OwnerId, includeOccupied: false);
        }

        return removed;
    }

    public IReadOnlyList<RouteReservation> GetActiveReservations() =>
        _inner.GetActiveReservations();
}
