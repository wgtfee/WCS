namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using Wcs.Core.RouteCenter;

public interface IUnifiedTransportDispatchEngine
{
    Task<TransportDispatchResult> DispatchAsync(
        TransportDispatchRequest request,
        CancellationToken cancellationToken = default);

    bool TryGetAssignment(string requestId, out TransportDispatchAssignment? assignment);
    bool Complete(string requestId);
}

/// <summary>
/// EMS/RGV 统一派单引擎。
/// 第二阶段改为仅预留滚动窗口，完整路径仍保留在 Assignment 中。
/// </summary>
public sealed class UnifiedTransportDispatchEngine : IUnifiedTransportDispatchEngine
{
    private readonly ITransportVehicleRegistry _vehicleRegistry;
    private readonly ITransportVehicleSelector _vehicleSelector;
    private readonly ITransportRouteCenter _routeCenter;
    private readonly IRouteReservationManager _reservationManager;
    private readonly ConcurrentDictionary<string, TransportDispatchAssignment> _assignments = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    public UnifiedTransportDispatchEngine(
        ITransportVehicleRegistry vehicleRegistry,
        ITransportVehicleSelector vehicleSelector,
        ITransportRouteCenter routeCenter,
        IRouteReservationManager reservationManager)
    {
        _vehicleRegistry = vehicleRegistry ?? throw new ArgumentNullException(nameof(vehicleRegistry));
        _vehicleSelector = vehicleSelector ?? throw new ArgumentNullException(nameof(vehicleSelector));
        _routeCenter = routeCenter ?? throw new ArgumentNullException(nameof(routeCenter));
        _reservationManager = reservationManager ?? throw new ArgumentNullException(nameof(reservationManager));
    }

    public async Task<TransportDispatchResult> DispatchAsync(
        TransportDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        if (_assignments.TryGetValue(request.RequestId, out var existing))
            return TransportDispatchResult.Succeeded(existing);

        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_assignments.TryGetValue(request.RequestId, out existing))
                return TransportDispatchResult.Succeeded(existing);

            var availableVehicles = _vehicleRegistry.GetAvailable(request);
            if (availableVehicles.Count == 0)
                return TransportDispatchResult.Failed("没有满足类型、状态和能力要求的可用 EMS/RGV");

            var candidates = _vehicleSelector.RankCandidates(request, availableVehicles);
            if (candidates.Count == 0)
                return TransportDispatchResult.Failed("可用车辆均无法到达取货点");

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var loadedRoute = _routeCenter.FindRoute(new TransportRouteRequest
                {
                    RequestId = $"{request.RequestId}:loaded:{candidate.Vehicle.VehicleId}",
                    FromNodeId = request.SourceNodeId,
                    ToNodeId = request.DestinationNodeId,
                    ObjectId = request.LoadId,
                    RequiredCapability = request.RequiredEdgeCapability,
                    Strategy = request.RouteStrategy,
                    Priority = request.Priority
                });

                if (!loadedRoute.Found)
                    continue;

                var fullEdgePath = candidate.PickupRoute.EdgePath
                    .Concat(loadedRoute.EdgePath)
                    .ToArray();

                var initialWindow = fullEdgePath
                    .Take(request.ReservationWindowEdges)
                    .ToArray();

                if (!_reservationManager.TryReserve(
                        request.RequestId,
                        initialWindow,
                        request.ReservationLease,
                        out var reservation) || reservation is null)
                {
                    continue;
                }

                if (!_vehicleRegistry.TryMarkAssigned(candidate.Vehicle.VehicleId))
                {
                    _reservationManager.Release(reservation.ReservationId);
                    continue;
                }

                var assignment = new TransportDispatchAssignment
                {
                    RequestId = request.RequestId,
                    VehicleId = candidate.Vehicle.VehicleId,
                    VehicleKind = candidate.Vehicle.Kind,
                    LoadId = request.LoadId,
                    PickupNodePath = candidate.PickupRoute.NodePath,
                    PickupEdgePath = candidate.PickupRoute.EdgePath,
                    LoadedNodePath = loadedRoute.NodePath,
                    LoadedEdgePath = loadedRoute.EdgePath,
                    ReservationId = reservation.ReservationId,
                    ReservationLease = request.ReservationLease,
                    ReservationWindowEdges = request.ReservationWindowEdges
                };

                if (_assignments.TryAdd(request.RequestId, assignment))
                    return TransportDispatchResult.Succeeded(assignment);

                _vehicleRegistry.TryMarkIdle(candidate.Vehicle.VehicleId);
                _reservationManager.Release(reservation.ReservationId);

                if (_assignments.TryGetValue(request.RequestId, out existing))
                    return TransportDispatchResult.Succeeded(existing);
            }

            return TransportDispatchResult.Failed("无车辆能够同时完成路径规划和初始滚动窗口预留");
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    public bool TryGetAssignment(string requestId, out TransportDispatchAssignment? assignment)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            assignment = null;
            return false;
        }

        return _assignments.TryGetValue(requestId, out assignment);
    }

    public bool Complete(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        if (!_assignments.TryRemove(requestId, out var assignment))
            return false;

        _reservationManager.Release(assignment.ReservationId);
        _vehicleRegistry.TryMarkIdle(assignment.VehicleId);
        return true;
    }

    private static void Validate(TransportDispatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
            throw new ArgumentException("RequestId 不能为空", nameof(request));
        if (string.IsNullOrWhiteSpace(request.SourceNodeId))
            throw new ArgumentException("SourceNodeId 不能为空", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DestinationNodeId))
            throw new ArgumentException("DestinationNodeId 不能为空", nameof(request));
        if (request.ReservationLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "ReservationLease 必须大于 0");
        if (request.ReservationWindowEdges <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "ReservationWindowEdges 必须大于 0");
    }
}
