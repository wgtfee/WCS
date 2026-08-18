namespace Wcs.Core.TransportScheduling;

using Wcs.Core.RouteCenter;

public interface IRouteReservationManager
{
    bool TryReserve(
        string ownerId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        out RouteReservation? reservation);

    bool TryExtend(
        string reservationId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        out RouteReservation? reservation);

    bool ReleaseEdges(string reservationId, IReadOnlyCollection<string> edgeIds);
    bool Renew(string reservationId, TimeSpan lease);
    bool TryGet(string reservationId, out RouteReservation? reservation);
    bool Release(string reservationId);
    int CleanupExpired(DateTime? nowUtc = null);
    IReadOnlyList<RouteReservation> GetActiveReservations();
}

/// <summary>
/// 路段原子预留管理器。
/// 第二阶段支持滚动窗口：释放已通过路段、向前扩展新路段并续租。
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
        ValidateLease(lease);

        var normalizedEdges = Normalize(edgeIds);

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

    public bool TryExtend(
        string reservationId,
        IReadOnlyCollection<string> edgeIds,
        TimeSpan lease,
        out RouteReservation? reservation)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            throw new ArgumentException("reservationId 不能为空", nameof(reservationId));
        ArgumentNullException.ThrowIfNull(edgeIds);
        ValidateLease(lease);

        var normalizedEdges = Normalize(edgeIds);

        lock (_sync)
        {
            var now = DateTime.UtcNow;
            CleanupExpiredUnsafe(now);

            if (!_reservations.TryGetValue(reservationId, out var current))
            {
                reservation = null;
                return false;
            }

            var additions = normalizedEdges
                .Where(edgeId => !current.EdgeIds.Contains(edgeId, StringComparer.Ordinal))
                .ToArray();

            if (additions.Any(edgeId =>
                    _edgeToReservation.TryGetValue(edgeId, out var ownerReservationId) &&
                    !string.Equals(ownerReservationId, reservationId, StringComparison.Ordinal)))
            {
                reservation = current;
                return false;
            }

            foreach (var edgeId in additions)
                _edgeToReservation[edgeId] = reservationId;

            reservation = current with
            {
                EdgeIds = current.EdgeIds.Concat(additions).Distinct(StringComparer.Ordinal).ToArray(),
                ExpiresAtUtc = now.Add(lease)
            };
            _reservations[reservationId] = reservation;

            if (additions.Length > 0)
                _routeCenter.OccupyPath(additions, current.OwnerId);

            return true;
        }
    }

    public bool ReleaseEdges(string reservationId, IReadOnlyCollection<string> edgeIds)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            return false;
        ArgumentNullException.ThrowIfNull(edgeIds);

        var normalizedEdges = Normalize(edgeIds);

        lock (_sync)
        {
            if (!_reservations.TryGetValue(reservationId, out var current))
                return false;

            var releasable = normalizedEdges
                .Where(edgeId => current.EdgeIds.Contains(edgeId, StringComparer.Ordinal))
                .ToArray();

            if (releasable.Length == 0)
                return true;

            foreach (var edgeId in releasable)
            {
                if (_edgeToReservation.TryGetValue(edgeId, out var ownerReservationId) &&
                    string.Equals(ownerReservationId, reservationId, StringComparison.Ordinal))
                {
                    _edgeToReservation.Remove(edgeId);
                }
            }

            var remaining = current.EdgeIds
                .Where(edgeId => !releasable.Contains(edgeId, StringComparer.Ordinal))
                .ToArray();

            _reservations[reservationId] = current with { EdgeIds = remaining };
            _routeCenter.ReleasePath(releasable, current.OwnerId);
            return true;
        }
    }

    public bool Renew(string reservationId, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
            return false;
        ValidateLease(lease);

        lock (_sync)
        {
            CleanupExpiredUnsafe(DateTime.UtcNow);
            if (!_reservations.TryGetValue(reservationId, out var current))
                return false;

            _reservations[reservationId] = current with
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(lease)
            };
            return true;
        }
    }

    public bool TryGet(string reservationId, out RouteReservation? reservation)
    {
        if (string.IsNullOrWhiteSpace(reservationId))
        {
            reservation = null;
            return false;
        }

        lock (_sync)
        {
            CleanupExpiredUnsafe(DateTime.UtcNow);
            return _reservations.TryGetValue(reservationId, out reservation);
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

        if (reservation.EdgeIds.Count > 0)
            _routeCenter.ReleasePath(reservation.EdgeIds, reservation.OwnerId);

        return true;
    }

    private static string[] Normalize(IEnumerable<string> edgeIds) =>
        edgeIds
            .Where(edgeId => !string.IsNullOrWhiteSpace(edgeId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void ValidateLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease), "lease 必须大于 0");
    }
}
