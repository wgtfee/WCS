namespace Wcs.Core.TransportScheduling;

public sealed record TransportVehiclePerformanceSnapshot
{
    public string VehicleId { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public TransportVehicleOperatingState State { get; init; }
    public int BatteryPercent { get; init; }
    public int CompletedTaskCount { get; init; }
    public int FaultedTaskCount { get; init; }
    public int WaitingTaskCount { get; init; }
    public double AverageCompletedDurationSeconds { get; init; }
}

public sealed record TransportPerformanceSnapshot
{
    public int OnlineVehicleCount { get; init; }
    public int IdleVehicleCount { get; init; }
    public int ExecutingVehicleCount { get; init; }
    public int ChargingVehicleCount { get; init; }
    public int LowBatteryVehicleCount { get; init; }
    public int TotalExecutionCount { get; init; }
    public int CompletedTaskCount { get; init; }
    public int FaultedTaskCount { get; init; }
    public int WaitingTaskCount { get; init; }
    public int ReassignmentCount { get; init; }
    public int ManualRecoveryCount { get; init; }
    public double FleetUtilizationPercent { get; init; }
    public double CompletionRatePercent { get; init; }
    public double AverageCompletedDurationSeconds { get; init; }
    public IReadOnlyList<TransportVehiclePerformanceSnapshot> Vehicles { get; init; } =
        Array.Empty<TransportVehiclePerformanceSnapshot>();
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ITransportPerformanceService
{
    TransportPerformanceSnapshot GetSnapshot();
}

/// <summary>
/// 第五阶段运行效率统计。
/// 当前使用执行快照计算实时指标，后续可将同一模型写入时序数据库或日报表。
/// </summary>
public sealed class TransportPerformanceService : ITransportPerformanceService
{
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportExecutionEngine _executions;
    private readonly ITransportChargingCoordinator _charging;
    private readonly ITransportTaskReassignmentService _reassignments;

    public TransportPerformanceService(
        ITransportVehicleRegistry vehicles,
        ITransportExecutionEngine executions,
        ITransportChargingCoordinator charging,
        ITransportTaskReassignmentService reassignments)
    {
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _charging = charging ?? throw new ArgumentNullException(nameof(charging));
        _reassignments = reassignments ?? throw new ArgumentNullException(nameof(reassignments));
    }

    public TransportPerformanceSnapshot GetSnapshot()
    {
        var vehicles = _vehicles.GetAll();
        var executions = _executions.GetAll();
        var reassignments = _reassignments.GetHistory();
        var completed = executions
            .Where(x => x.State == TransportExecutionState.Completed)
            .ToArray();

        var onlineCount = vehicles.Count(x => x.IsOnline);
        var executingCount = vehicles.Count(x => x.State == TransportVehicleOperatingState.Executing);
        var chargingCount = vehicles.Count(x =>
            x.State is (
                TransportVehicleOperatingState.Charging or
                TransportVehicleOperatingState.ChargingRequested or
                TransportVehicleOperatingState.WaitingForCharge));

        var activeUtilized = executingCount + chargingCount;
        var faultedCount = executions.Count(x => x.State == TransportExecutionState.Faulted);
        var waitingCount = executions.Count(x =>
            x.State is (
                TransportExecutionState.WaitingForRoute or
                TransportExecutionState.Paused));

        var vehicleMetrics = vehicles
            .OrderBy(x => x.VehicleId, StringComparer.Ordinal)
            .Select(vehicle =>
            {
                var own = executions
                    .Where(x => string.Equals(x.VehicleId, vehicle.VehicleId, StringComparison.Ordinal))
                    .ToArray();
                var ownCompleted = own
                    .Where(x => x.State == TransportExecutionState.Completed)
                    .ToArray();

                return new TransportVehiclePerformanceSnapshot
                {
                    VehicleId = vehicle.VehicleId,
                    Kind = vehicle.Kind,
                    State = vehicle.State,
                    BatteryPercent = vehicle.BatteryPercent,
                    CompletedTaskCount = ownCompleted.Length,
                    FaultedTaskCount = own.Count(x => x.State == TransportExecutionState.Faulted),
                    WaitingTaskCount = own.Count(x =>
                        x.State is (
                            TransportExecutionState.WaitingForRoute or
                            TransportExecutionState.Paused)),
                    AverageCompletedDurationSeconds = AverageDurationSeconds(ownCompleted)
                };
            })
            .ToArray();

        return new TransportPerformanceSnapshot
        {
            OnlineVehicleCount = onlineCount,
            IdleVehicleCount = vehicles.Count(x => x.State == TransportVehicleOperatingState.Idle),
            ExecutingVehicleCount = executingCount,
            ChargingVehicleCount = chargingCount,
            LowBatteryVehicleCount = vehicles.Count(x =>
                x.BatteryPercent <= _charging.Policy.ChargeThresholdPercent),
            TotalExecutionCount = executions.Count,
            CompletedTaskCount = completed.Length,
            FaultedTaskCount = faultedCount,
            WaitingTaskCount = waitingCount,
            ReassignmentCount = reassignments.Count(x =>
                x.Decision == TransportReassignmentDecision.Reassigned),
            ManualRecoveryCount = reassignments.Count(x =>
                x.Decision == TransportReassignmentDecision.ManualRecoveryRequired),
            FleetUtilizationPercent = onlineCount == 0
                ? 0
                : Math.Round(activeUtilized * 100d / onlineCount, 2),
            CompletionRatePercent = executions.Count == 0
                ? 0
                : Math.Round(completed.Length * 100d / executions.Count, 2),
            AverageCompletedDurationSeconds = AverageDurationSeconds(completed),
            Vehicles = vehicleMetrics
        };
    }

    private static double AverageDurationSeconds(
        IReadOnlyCollection<TransportExecutionSnapshot> executions)
    {
        if (executions.Count == 0)
            return 0;

        return Math.Round(
            executions.Average(x =>
                Math.Max(0, (x.UpdatedAtUtc - x.CreatedAtUtc).TotalSeconds)),
            2);
    }
}
