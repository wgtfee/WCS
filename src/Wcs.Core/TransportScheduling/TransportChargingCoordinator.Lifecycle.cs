namespace Wcs.Core.TransportScheduling;

public sealed partial class TransportChargingCoordinator
{
    public bool ConfirmArrived(string planId)
    {
        lock (_sync)
        {
            if (!_plans.TryGetValue(planId, out var current) ||
                current.State != TransportChargingPlanState.Reserved)
            {
                return false;
            }

            if (!_stations.TryGetValue(current.StationId, out var station) || !station.IsOnline)
                return false;

            var chargingCount = _plans.Values.Count(x =>
                !x.IsTerminal &&
                x.State == TransportChargingPlanState.Charging &&
                string.Equals(x.StationId, current.StationId, StringComparison.Ordinal));

            if (chargingCount >= station.Capacity)
                return false;

            if (!_vehicles.TryMarkCharging(current.VehicleId, station.NodeId))
                return false;

            _plans[planId] = current with
            {
                State = TransportChargingPlanState.Charging,
                Message = "车辆已到达充电位并开始充电",
                UpdatedAtUtc = DateTime.UtcNow
            };

            return true;
        }
    }

    public bool Complete(string planId, int batteryPercent)
    {
        if (batteryPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(batteryPercent));

        lock (_sync)
        {
            if (!_plans.TryGetValue(planId, out var current) ||
                current.State != TransportChargingPlanState.Charging)
            {
                return false;
            }

            if (!_vehicles.TryFinishCharging(
                    current.VehicleId,
                    batteryPercent,
                    Policy.MinimumDispatchBatteryPercent))
            {
                return false;
            }

            _plans[planId] = current with
            {
                State = TransportChargingPlanState.Completed,
                EndBatteryPercent = batteryPercent,
                Message = batteryPercent >= Policy.ResumeBatteryPercent
                    ? "充电完成，车辆恢复空闲"
                    : "充电结束，但电量未达到推荐恢复阈值",
                UpdatedAtUtc = DateTime.UtcNow
            };

            PromoteNextWaitingUnsafe(current.StationId);
            return true;
        }
    }

    public bool Cancel(string planId, string? reason = null)
    {
        lock (_sync)
        {
            if (!_plans.TryGetValue(planId, out var current) || current.IsTerminal)
                return false;

            _plans[planId] = current with
            {
                State = TransportChargingPlanState.Cancelled,
                Message = string.IsNullOrWhiteSpace(reason) ? "充电计划已取消" : reason,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _vehicles.TryMarkIdle(current.VehicleId);
            PromoteNextWaitingUnsafe(current.StationId);
            return true;
        }
    }

    private TransportChargingStationSnapshot ToSnapshot(TransportChargingStationDefinition station)
    {
        var active = _plans.Values
            .Where(x =>
                !x.IsTerminal &&
                string.Equals(x.StationId, station.StationId, StringComparison.Ordinal))
            .ToArray();

        return new TransportChargingStationSnapshot
        {
            StationId = station.StationId,
            NodeId = station.NodeId,
            Name = station.Name,
            IsOnline = station.IsOnline,
            Capacity = station.Capacity,
            ReservedCount = active.Count(x => x.State == TransportChargingPlanState.Reserved),
            ChargingCount = active.Count(x => x.State == TransportChargingPlanState.Charging),
            QueueLength = active.Count(x => x.State == TransportChargingPlanState.WaitingForStation),
            VehicleIds = active
                .Select(x => x.VehicleId)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private int CountReservedSlots(string stationId) =>
        _plans.Values.Count(x =>
            !x.IsTerminal &&
            string.Equals(x.StationId, stationId, StringComparison.Ordinal) &&
            x.State is TransportChargingPlanState.Reserved or TransportChargingPlanState.Charging);

    private int CountActivePlans(string stationId) =>
        _plans.Values.Count(x =>
            !x.IsTerminal &&
            string.Equals(x.StationId, stationId, StringComparison.Ordinal));

    private void PromoteNextWaitingUnsafe(string stationId)
    {
        if (!_stations.TryGetValue(stationId, out var station))
            return;

        while (CountReservedSlots(stationId) < station.Capacity)
        {
            var waiting = _plans.Values
                .Where(x =>
                    !x.IsTerminal &&
                    x.State == TransportChargingPlanState.WaitingForStation &&
                    string.Equals(x.StationId, stationId, StringComparison.Ordinal))
                .OrderByDescending(x => x.IsCritical)
                .ThenBy(x => x.StartBatteryPercent)
                .ThenBy(x => x.CreatedAtUtc)
                .FirstOrDefault();

            if (waiting is null)
                break;

            if (!_vehicles.TryMarkChargingRequested(waiting.VehicleId, waitingForStation: false))
            {
                _plans[waiting.PlanId] = waiting with
                {
                    State = TransportChargingPlanState.Faulted,
                    Message = "车辆状态已变化，无法升级充电预留",
                    UpdatedAtUtc = DateTime.UtcNow
                };
                continue;
            }

            _plans[waiting.PlanId] = waiting with
            {
                State = TransportChargingPlanState.Reserved,
                Message = "充电位已释放，计划升级为已预留",
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
    }

    private static void ValidatePolicy(TransportChargingPolicy policy)
    {
        if (policy.CriticalThresholdPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.ChargeThresholdPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.ResumeBatteryPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.MinimumDispatchBatteryPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (policy.CriticalThresholdPercent > policy.ChargeThresholdPercent)
            throw new ArgumentException("临界电量不能高于充电阈值", nameof(policy));
    }
}
