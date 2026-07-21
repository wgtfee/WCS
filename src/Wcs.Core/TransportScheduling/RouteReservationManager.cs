namespace Wcs.Core.TransportScheduling;

using Wcs.Core.RouteCenter;

public interface IRouteReservationManager
{
    bool TryReserve(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        out RouteReservation? reservation);

    bool Release(string reservationId);
    int CleanupExpired(DateTime? nowUtc = null);
    IReadOnlyList<RouteReservation> GetActiveReservations();
}

/// <summary>
/// 路段原子预留管理器。一次派单的全部路段要么全部成功，要么全部失败。
/// 第一阶段采用进程内锁，后续由持久化快照和恢复服务接管重启恢复。
/// </summary>
public sealed class InMemoryRouteReservationManager : IRouteReservationManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _edgeToReservation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouteReservation> _reservations = new(StringComparer.Ordinal);
    private readonly ITransportRouteCenter _routeCenter;

    public InMemoryRouteReservationManager(ITransportRouteCenter routeCenter)
    {
        _routeCenter = routeCenter ?? throw new ArgumentNullException(nameof(routeCenter));
    }

    public bool TryReserve(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        out RouteReservation? reservation)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("ownerId 不能为空", nameof(ownerId));
        ArgumentNullException.ThrowIfNull(edgeIds);
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease), "lease 必须大于 0");

        var normalizedEdges = edgeIds
            .Where(edgeId => !string.IsNullOrWhiteSpace(edgeId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        lock (_sync)
        {
            var now = DateTime.UtcNow;
            CleanupExpiredUnsafe(now);

            if (normalizedEdges.Any(edgeId => _edgeToReservation.ContainsKey(edgeId)))
            {
                reservation = null;
                return false;
            }

            reservation = new RouteReservation
            {
                OwnerId = ownerId,
                EdgeIds = normalizedEdges,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(lease)
            };

            _reservations.Add(reservation.ReservationId, reservation);
            foreach (var edgeId in normalizedEdges)
                _edgeToReservation.Add(edgeId, reservation.ReservationId);

            _routeCenter.OccupyPath(normalizedEdges, ownerId);
            return true;
        }
    }

    public bool Release(string reservationId)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            return false;

        lock (_sync)
        {
            return ReleaseUnsafe(reservationId);
        }
    }

    public int CleanupExpired(DateTime? nowUtc = null)
    {
        lock (_sync)
        {
            return CleanupExpiredUnsafe(nowUtc ?? DateTime.UtcNow);
        }
    }

    public IReadOnlyList<RouteReservation> GetActiveReservations()
    {
        lock (_sync)
        {
            CleanupExpiredUnsafe(DateTime.UtcNow);
            return _reservations.Values
                .OrderBy(r => r.ExpiresAtUtc)
                .ToList();
        }
    }

    private int CleanupExpiredUnsafe(DateTime nowUtc)
    {
        var expiredIds = _reservations.Values
            .Where(r => r.ExpiresAtUtc <= nowUtc)
            .Select(r => r.ReservationId)
            .ToArray();

        foreach (var reservationId in expiredIds)
            ReleaseUnsafe(reservationId);

        return expiredIds.Length;
    }

    private bool ReleaseUnsafe(string reservationId)
    {
        if (!_reservations.Remove(reservationId, out var reservation))
            return false;

        foreach (var edgeId in reservation.EdgeIds)
        {
            if (_edgeToReservation.TryGetValue(edgeId, out var ownerReservationId) &&
                string.Equals(ownerReservationId, reservationId, StringComparison.Ordinal))
            {
                _edgeToReservation.Remove(edgeId);
            }
        }

        _routeCenter.ReleasePath(reservation.EdgeIds, reservation.OwnerId);
        return true;
    }
}
