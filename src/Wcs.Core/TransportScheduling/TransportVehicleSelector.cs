namespace Wcs.Core.TransportScheduling;

using Wcs.Core.RouteCenter;

public interface ITransportVehicleSelector
{
    IReadOnlyList<TransportVehicleCandidate> RankCandidates(
        TransportDispatchRequest request,
        IReadOnlyCollection<TransportVehicleSnapshot> vehicles);
}

/// <summary>
/// 第一阶段默认车辆选择策略：空驶路径优先，其次考虑任务负载和电量。
/// </summary>
public sealed class DefaultTransportVehicleSelector : ITransportVehicleSelector
{
    private readonly ITransportRouteCenter _routeCenter;

    public DefaultTransportVehicleSelector(ITransportRouteCenter routeCenter)
    {
        _routeCenter = routeCenter ?? throw new ArgumentNullException(nameof(routeCenter));
    }

    public IReadOnlyList<TransportVehicleCandidate> RankCandidates(
        TransportDispatchRequest request,
        IReadOnlyCollection<TransportVehicleSnapshot> vehicles)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(vehicles);

        var candidates = new List<TransportVehicleCandidate>();

        foreach (var vehicle in vehicles)
        {
            var pickupRoute = _routeCenter.FindRoute(new TransportRouteRequest
            {
                RequestId = $"{request.RequestId}:pickup:{vehicle.VehicleId}",
                FromNodeId = vehicle.CurrentNodeId,
                ToNodeId = request.SourceNodeId,
                ObjectId = request.LoadId,
                RequiredCapability = request.RequiredEdgeCapability,
                Strategy = request.RouteStrategy,
                Priority = request.Priority
            });

            if (!pickupRoute.Found)
                continue;

            var score = checked(
                pickupRoute.TotalWeight * 100 +
                vehicle.ActiveTaskCount * 1_000 +
                Math.Max(0, 100 - vehicle.BatteryPercent));

            candidates.Add(new TransportVehicleCandidate
            {
                Vehicle = vehicle,
                PickupRoute = pickupRoute,
                Score = score
            });
        }

        return candidates
            .OrderBy(c => c.Score)
            .ThenByDescending(c => c.Vehicle.BatteryPercent)
            .ThenBy(c => c.Vehicle.VehicleId, StringComparer.Ordinal)
            .ToList();
    }
}
