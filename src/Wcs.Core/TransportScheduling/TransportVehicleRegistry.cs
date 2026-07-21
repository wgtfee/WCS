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
}

/// <summary>
/// 统一车辆状态注册表。拒绝旧版本覆盖新状态，并通过 CAS 更新避免并发丢失。
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
            .Where(v => request.AllowedVehicleKinds is null || request.AllowedVehicleKinds.Contains(v.Kind))
            .Where(v => (v.Capabilities & request.RequiredCapability) == request.RequiredCapability)
            .OrderBy(v => v.VehicleId, StringComparer.Ordinal)
            .ToList();
    }

    public bool TryMarkAssigned(string vehicleId) =>
        TryTransition(vehicleId, TransportVehicleOperatingState.Idle, TransportVehicleOperatingState.Executing, 1);

    public bool TryMarkIdle(string vehicleId)
    {
        while (true)
        {
            if (!_vehicles.TryGetValue(vehicleId, out var current))
                return false;

            if (current.State == TransportVehicleOperatingState.Idle)
                return true;
            if (current.State != TransportVehicleOperatingState.Executing)
                return false;

            var next = current with
            {
                State = TransportVehicleOperatingState.Idle,
                ActiveTaskCount = Math.Max(0, current.ActiveTaskCount - 1),
                Version = current.Version + 1,
                UpdatedAtUtc = DateTime.UtcNow
            };

            if (_vehicles.TryUpdate(vehicleId, next, current))
                return true;
        }
    }

    private bool TryTransition(
        string vehicleId,
        TransportVehicleOperatingState expected,
        TransportVehicleOperatingState target,
        int taskDelta)
    {
        while (true)
        {
            if (!_vehicles.TryGetValue(vehicleId, out var current))
                return false;

            if (!current.IsOnline || current.State != expected)
                return false;

            var next = current with
            {
                State = target,
                ActiveTaskCount = Math.Max(0, current.ActiveTaskCount + taskDelta),
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
