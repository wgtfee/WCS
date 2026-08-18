namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public interface ITransportVehicleRegistry
{
    bool Upsert(TransportVehicleSnapshot snapshot);
    bool TryGet(string vehicleId, out TransportVehicleSnapshot? snapshot);
    IReadOnlyList<TransportVehicleSnapshot> GetAll();
    IReadOnlyList<TransportVehicleSnapshot> GetAvailable(TransportDispatchRequest request);
    bool TryMarkAssigned(string vehicleId);
    bool TryMarkIdle(string vehicleId);
    bool TryMarkChargingRequested(string vehicleId, bool waitingForStation);
    bool TryMarkCharging(string vehicleId, string stationNodeId);
    bool TryFinishCharging(string vehicleId, int batteryPercent, int minimumDispatchBatteryPercent);
    bool TryMarkFaulted(string vehicleId);
    bool TryReleaseFaultedTask(string vehicleId);
}

/// <summary>
/// 统一车辆状态注册表。拒绝旧版本覆盖新状态，并通过 CAS 更新避免并发丢失。
/// 第五阶段增加最低电量保护、充电状态原子迁移和指定车辆过滤。
/// </summary>
public sealed class InMemoryTransportVehicleRegistry : ITransportVehicleRegistry
{
    private readonly ConcurrentDictionary<string, TransportVehicleSnapshot> _vehicles = new(StringComparer.Ordinal);

    public bool Upsert(TransportVehicleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot);

        while (true)
        {
            if (!_vehicles.TryGetValue(snapshot.VehicleId, out var current))
                return _vehicles.TryAdd(snapshot.VehicleId, snapshot);

            if (snapshot.Version < current.Version)
                return false;

            if (snapshot.Version == current.Version && snapshot.UpdatedAtUtc < current.UpdatedAtUtc)
                return false;

            if (_vehicles.TryUpdate(snapshot.VehicleId, snapshot, current))
                return true;
        }
    }

    public bool TryGet(string vehicleId, out TransportVehicleSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            snapshot = null;
            return false;
        }

        return _vehicles.TryGetValue(vehicleId, out snapshot);
    }

    public IReadOnlyList<TransportVehicleSnapshot> GetAll() =>
        _vehicles.Values.OrderBy(v => v.VehicleId, StringComparer.Ordinal).ToList();

    public IReadOnlyList<TransportVehicleSnapshot> GetAvailable(TransportDispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _vehicles.Values
            .Where(v => v.CanAcceptTask)
            .Where(v => string.IsNullOrWhiteSpace(request.RequiredVehicleId) ||
                        string.Equals(v.VehicleId, request.RequiredVehicleId, StringComparison.Ordinal))
            .Where(v => request.AllowedVehicleKinds is null || request.AllowedVehicleKinds.Contains(v.Kind))
            .Where(v => (v.Capabilities & request.RequiredCapability) == request.RequiredCapability)
            .Where(v => request.AllowLowBatteryOverride || v.BatteryPercent >= request.MinimumBatteryPercent)
            .OrderBy(v => v.VehicleId, StringComparer.Ordinal)
            .ToList();
    }

    public bool TryMarkAssigned(string vehicleId) =>
        TryTransition(
            vehicleId,
            new[] { TransportVehicleOperatingState.Idle },
            TransportVehicleOperatingState.Executing,
            taskDelta: 1);

    public bool TryMarkChargingRequested(string vehicleId, bool waitingForStation) =>
        TryTransition(
            vehicleId,
            new[]
            {
                TransportVehicleOperatingState.Idle,
                TransportVehicleOperatingState.WaitingForCharge
            },
            waitingForStation
                ? TransportVehicleOperatingState.WaitingForCharge
                : TransportVehicleOperatingState.ChargingRequested);

    public bool TryMarkCharging(string vehicleId, string stationNodeId)
    {
        if (string.IsNullOrWhiteSpace(stationNodeId))
            return false;

        return TryTransition(
            vehicleId,
            new[] { TransportVehicleOperatingState.ChargingRequested },
            TransportVehicleOperatingState.Charging,
            currentNodeId: stationNodeId);
    }

    public bool TryFinishCharging(
        string vehicleId,
        int batteryPercent,
        int minimumDispatchBatteryPercent)
    {
        if (batteryPercent is < 0 or > 100)
            return false;
        if (minimumDispatchBatteryPercent is < 0 or > 100)
            return false;

        return TryTransition(
            vehicleId,
            new[] { TransportVehicleOperatingState.Charging },
            batteryPercent >= minimumDispatchBatteryPercent
                ? TransportVehicleOperatingState.Idle
                : TransportVehicleOperatingState.WaitingForCharge,
            batteryPercent: batteryPercent);
    }

    public bool TryMarkFaulted(string vehicleId) =>
        TryTransition(
            vehicleId,
            new[]
            {
                TransportVehicleOperatingState.Offline,
                TransportVehicleOperatingState.Idle,
                TransportVehicleOperatingState.Executing,
                TransportVehicleOperatingState.ChargingRequested,
                TransportVehicleOperatingState.WaitingForCharge,
                TransportVehicleOperatingState.Charging,
                TransportVehicleOperatingState.Maintenance
            },
            TransportVehicleOperatingState.Faulted,
            allowAlreadyTarget: true,
            requireOnline: false);

    public bool TryReleaseFaultedTask(string vehicleId)
    {
        while (true)
        {
            if (!_vehicles.TryGetValue(vehicleId, out var current) ||
                current.State != TransportVehicleOperatingState.Faulted)
            {
                return false;
            }

            var next = current with
            {
                ActiveTaskCount = Math.Max(0, current.ActiveTaskCount - 1),
                Version = current.Version + 1,
                UpdatedAtUtc = DateTime.UtcNow
            };

            if (_vehicles.TryUpdate(vehicleId, next, current))
                return true;
        }
    }

    public bool TryMarkIdle(string vehicleId) =>
        TryTransition(
            vehicleId,
            new[]
            {
                TransportVehicleOperatingState.Executing,
                TransportVehicleOperatingState.Charging,
                TransportVehicleOperatingState.ChargingRequested,
                TransportVehicleOperatingState.WaitingForCharge
            },
            TransportVehicleOperatingState.Idle,
            decrementTaskWhenExecuting: true,
            allowAlreadyTarget: true);

    private bool TryTransition(
        string vehicleId,
        IReadOnlyCollection<TransportVehicleOperatingState> expectedStates,
        TransportVehicleOperatingState target,
        int taskDelta = 0,
        string? currentNodeId = null,
        int? batteryPercent = null,
        bool decrementTaskWhenExecuting = false,
        bool allowAlreadyTarget = false,
        bool requireOnline = true)
    {
        while (true)
        {
            if (!_vehicles.TryGetValue(vehicleId, out var current))
                return false;

            if (allowAlreadyTarget && current.State == target)
                return true;

            if ((requireOnline && !current.IsOnline) || !expectedStates.Contains(current.State))
                return false;

            var effectiveTaskDelta = decrementTaskWhenExecuting &&
                                     current.State == TransportVehicleOperatingState.Executing
                ? -1
                : taskDelta;

            var next = current with
            {
                State = target,
                ActiveTaskCount = Math.Max(0, current.ActiveTaskCount + effectiveTaskDelta),
                CurrentNodeId = currentNodeId ?? current.CurrentNodeId,
                BatteryPercent = batteryPercent ?? current.BatteryPercent,
                Version = current.Version + 1,
                UpdatedAtUtc = DateTime.UtcNow
            };

            if (_vehicles.TryUpdate(vehicleId, next, current))
                return true;
        }
    }

    private static void Validate(TransportVehicleSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.VehicleId))
            throw new ArgumentException("VehicleId 不能为空", nameof(snapshot));
        if (snapshot.BatteryPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "BatteryPercent 必须在 0 到 100 之间");
        if (snapshot.ActiveTaskCount < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "ActiveTaskCount 不能小于 0");
    }
}
