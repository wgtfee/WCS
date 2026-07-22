namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using Wcs.Core.RouteCenter;

public enum TransportChargingPlanState
{
    WaitingForStation = 0,
    Reserved = 1,
    Charging = 2,
    Completed = 3,
    Cancelled = 4,
    Faulted = 5
}

public sealed record TransportChargingPolicy
{
    public int ChargeThresholdPercent { get; init; } = 30;
    public int CriticalThresholdPercent { get; init; } = 15;
    public int ResumeBatteryPercent { get; init; } = 80;
    public int MinimumDispatchBatteryPercent { get; init; } = 20;
}

public sealed record TransportChargingStationDefinition
{
    public string StationId { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsOnline { get; init; } = true;
    public int Capacity { get; init; } = 1;
    public IReadOnlyList<TransportVehicleKind> SupportedVehicleKinds { get; init; } =
        new[] { TransportVehicleKind.Ems, TransportVehicleKind.Rgv };
}

public sealed record TransportChargingStationSnapshot
{
    public string StationId { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsOnline { get; init; }
    public int Capacity { get; init; }
    public int ReservedCount { get; init; }
    public int ChargingCount { get; init; }
    public int QueueLength { get; init; }
    public IReadOnlyList<string> VehicleIds { get; init; } = Array.Empty<string>();
}

public sealed record TransportChargingPlan
{
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N");
    public string VehicleId { get; init; } = string.Empty;
    public string StationId { get; init; } = string.Empty;
    public TransportChargingPlanState State { get; init; }
    public int StartBatteryPercent { get; init; }
    public int? EndBatteryPercent { get; init; }
    public IReadOnlyList<string> NodePath { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EdgePath { get; init; } = Array.Empty<string>();
    public bool IsCritical { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public bool IsTerminal =>
        State is TransportChargingPlanState.Completed or
            TransportChargingPlanState.Cancelled or
            TransportChargingPlanState.Faulted;
}

public sealed record TransportChargingEvaluation
{
    public string VehicleId { get; init; } = string.Empty;
    public bool RequiresCharge { get; init; }
    public bool PlanCreated { get; init; }
    public bool IsCritical { get; init; }
    public string Message { get; init; } = string.Empty;
    public TransportChargingPlan? Plan { get; init; }
}

public interface ITransportChargingCoordinator
{
    TransportChargingPolicy Policy { get; }
    void RegisterStation(TransportChargingStationDefinition station);
    bool RemoveStation(string stationId);
    IReadOnlyList<TransportChargingStationSnapshot> GetStations();
    IReadOnlyList<TransportChargingPlan> GetPlans();
    TransportChargingEvaluation EvaluateVehicle(string vehicleId);
    IReadOnlyList<TransportChargingEvaluation> EvaluateFleet();
    bool ConfirmArrived(string planId);
    bool Complete(string planId, int batteryPercent);
    bool Cancel(string planId, string? reason = null);
}

/// <summary>
/// 第五阶段充电调度器。
/// 只对空闲车辆自动建立充电计划；执行中车辆低电量只返回告警，不改变当前运输任务。
/// 充电位按 Capacity 预留，超出容量的车辆进入站点等待队列。
/// </summary>
public sealed partial class TransportChargingCoordinator : ITransportChargingCoordinator
{
    private readonly object _sync = new();
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportRouteCenter _routeCenter;
    private readonly Dictionary<string, TransportChargingStationDefinition> _stations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransportChargingPlan> _plans = new(StringComparer.Ordinal);

    public TransportChargingCoordinator(
        ITransportVehicleRegistry vehicles,
        ITransportRouteCenter routeCenter)
        : this(vehicles, routeCenter, new TransportChargingPolicy())
    {
    }

    public TransportChargingCoordinator(
        ITransportVehicleRegistry vehicles,
        ITransportRouteCenter routeCenter,
        TransportChargingPolicy policy)
    {
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _routeCenter = routeCenter ?? throw new ArgumentNullException(nameof(routeCenter));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ValidatePolicy(Policy);
    }

    public TransportChargingPolicy Policy { get; }

    public void RegisterStation(TransportChargingStationDefinition station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (string.IsNullOrWhiteSpace(station.StationId))
            throw new ArgumentException("StationId 不能为空", nameof(station));
        if (string.IsNullOrWhiteSpace(station.NodeId))
            throw new ArgumentException("NodeId 不能为空", nameof(station));
        if (station.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(station), "Capacity 必须大于 0");

        lock (_sync)
            _stations[station.StationId] = station;
    }

    public bool RemoveStation(string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
            return false;

        lock (_sync)
        {
            if (_plans.Values.Any(x =>
                    !x.IsTerminal &&
                    string.Equals(x.StationId, stationId, StringComparison.Ordinal)))
            {
                return false;
            }

            return _stations.Remove(stationId);
        }
    }

    public IReadOnlyList<TransportChargingStationSnapshot> GetStations()
    {
        lock (_sync)
        {
            return _stations.Values
                .OrderBy(x => x.StationId, StringComparer.Ordinal)
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    public IReadOnlyList<TransportChargingPlan> GetPlans() =>
        _plans.Values
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToArray();

    public TransportChargingEvaluation EvaluateVehicle(string vehicleId)
    {
        if (!_vehicles.TryGet(vehicleId, out var vehicle) || vehicle is null)
        {
            return Evaluation(vehicleId, false, false, false, "车辆不存在");
        }

        var critical = vehicle.BatteryPercent <= Policy.CriticalThresholdPercent;
        if (vehicle.BatteryPercent > Policy.ChargeThresholdPercent)
        {
            return Evaluation(vehicleId, false, false, false, "电量高于充电阈值");
        }

        var existing = FindActivePlan(vehicleId);
        if (existing is not null)
        {
            return Evaluation(
                vehicleId,
                true,
                false,
                critical,
                "车辆已有活动充电计划",
                existing);
        }

        if (!vehicle.IsOnline)
        {
            return Evaluation(vehicleId, true, false, critical, "车辆离线，无法自动调度充电");
        }

        if (vehicle.State is not (
                TransportVehicleOperatingState.Idle or
                TransportVehicleOperatingState.WaitingForCharge))
        {
            return Evaluation(
                vehicleId,
                true,
                false,
                critical,
                critical
                    ? "执行中车辆达到临界电量，完成当前安全动作后必须人工处理"
                    : "车辆非空闲，暂缓充电");
        }

        lock (_sync)
        {
            var concurrentExisting = FindActivePlan(vehicleId);
            if (concurrentExisting is not null)
            {
                return Evaluation(
                    vehicleId,
                    true,
                    false,
                    critical,
                    "车辆已有活动充电计划",
                    concurrentExisting);
            }

            var candidates = _stations.Values
                .Where(x => x.IsOnline)
                .Where(x => x.SupportedVehicleKinds.Contains(vehicle.Kind))
                .Select(station =>
                {
                    var route = _routeCenter.FindRoute(new TransportRouteRequest
                    {
                        RequestId = $"charge:{vehicle.VehicleId}:{station.StationId}",
                        FromNodeId = vehicle.CurrentNodeId,
                        ToNodeId = station.NodeId,
                        Strategy = TransportRouteStrategy.LeastCongested
                    });
                    return (Station: station, Route: route);
                })
                .Where(x => x.Route.Found)
                .OrderBy(x => x.Route.TotalWeight)
                .ThenBy(x => CountActivePlans(x.Station.StationId))
                .ThenBy(x => x.Station.StationId, StringComparer.Ordinal)
                .ToArray();

            if (candidates.Length == 0)
            {
                return Evaluation(vehicleId, true, false, critical, "没有在线且可达的充电站");
            }

            var selected = candidates[0];
            var hasCapacity = CountReservedSlots(selected.Station.StationId) < selected.Station.Capacity;

            if (!_vehicles.TryMarkChargingRequested(vehicle.VehicleId, waitingForStation: !hasCapacity))
            {
                return Evaluation(vehicleId, true, false, critical, "车辆状态已变化，充电计划未创建");
            }

            var plan = new TransportChargingPlan
            {
                VehicleId = vehicle.VehicleId,
                StationId = selected.Station.StationId,
                State = hasCapacity
                    ? TransportChargingPlanState.Reserved
                    : TransportChargingPlanState.WaitingForStation,
                StartBatteryPercent = vehicle.BatteryPercent,
                NodePath = selected.Route.NodePath,
                EdgePath = selected.Route.EdgePath,
                IsCritical = critical,
                Message = hasCapacity ? "已预留充电位" : "充电位已满，进入等待队列"
            };

            _plans[plan.PlanId] = plan;
            return Evaluation(vehicleId, true, true, critical, plan.Message, plan);
        }
    }

    public IReadOnlyList<TransportChargingEvaluation> EvaluateFleet() =>
        _vehicles.GetAll()
            .Where(x => x.BatteryPercent <= Policy.ChargeThresholdPercent)
            .OrderBy(x => x.BatteryPercent)
            .ThenBy(x => x.VehicleId, StringComparer.Ordinal)
            .Select(x => EvaluateVehicle(x.VehicleId))
            .ToArray();

    private TransportChargingPlan? FindActivePlan(string vehicleId) =>
        _plans.Values.FirstOrDefault(x =>
            !x.IsTerminal &&
            string.Equals(x.VehicleId, vehicleId, StringComparison.Ordinal));

    private static TransportChargingEvaluation Evaluation(
        string vehicleId,
        bool requiresCharge,
        bool planCreated,
        bool critical,
        string message,
        TransportChargingPlan? plan = null) => new()
    {
        VehicleId = vehicleId,
        RequiresCharge = requiresCharge,
        PlanCreated = planCreated,
        IsCritical = critical,
        Message = message,
        Plan = plan
    };
}
