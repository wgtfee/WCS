namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public sealed record TransportVehicleDefinition
{
    public string VehicleId { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public string InitialNodeId { get; init; } = string.Empty;
    public TransportVehicleCapability Capabilities { get; init; } = TransportVehicleCapability.Carry;
    public int InitialBatteryPercent { get; init; } = 100;
    public bool Enabled { get; init; } = true;
}

public sealed record TransportDriverEndpointDefinition
{
    public string DriverId { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public string Protocol { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public string? StationName { get; init; }
    public int PollIntervalMs { get; init; } = 200;
    public int CommandTimeoutMs { get; init; } = 5000;
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record TransportRuntimeConfiguration
{
    public string ConfigurationId { get; init; } = "runtime";
    public long Version { get; init; }
    public TransportChargingPolicy ChargingPolicy { get; init; } = new();
    public IReadOnlyList<TransportTrafficResourceDefinition> TrafficResources { get; init; } = Array.Empty<TransportTrafficResourceDefinition>();
    public IReadOnlyList<TransportChargingStationDefinition> ChargingStations { get; init; } = Array.Empty<TransportChargingStationDefinition>();
    public IReadOnlyList<TransportVehicleDefinition> Vehicles { get; init; } = Array.Empty<TransportVehicleDefinition>();
    public IReadOnlyList<TransportDriverEndpointDefinition> Drivers { get; init; } = Array.Empty<TransportDriverEndpointDefinition>();
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportConfigurationSaveResult
{
    public bool Success { get; init; }
    public bool VersionConflict { get; init; }
    public TransportRuntimeConfiguration? Configuration { get; init; }
    public string? Error { get; init; }

    public static TransportConfigurationSaveResult Saved(TransportRuntimeConfiguration configuration) =>
        new() { Success = true, Configuration = configuration };

    public static TransportConfigurationSaveResult Conflict(TransportRuntimeConfiguration? current) =>
        new() { VersionConflict = true, Configuration = current, Error = "配置版本已变化，请刷新后重试" };

    public static TransportConfigurationSaveResult Failed(string error) =>
        new() { Error = error };
}

public interface ITransportConfigurationStore
{
    Task<TransportRuntimeConfiguration?> LoadAsync(
        string configurationId = "runtime",
        CancellationToken cancellationToken = default);

    Task<TransportConfigurationSaveResult> SaveAsync(
        TransportRuntimeConfiguration configuration,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryTransportConfigurationStore : ITransportConfigurationStore
{
    private readonly ConcurrentDictionary<string, TransportRuntimeConfiguration> _configurations = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<TransportRuntimeConfiguration?> LoadAsync(
        string configurationId = "runtime",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _configurations.TryGetValue(configurationId, out var configuration);
        return Task.FromResult(configuration);
    }

    public Task<TransportConfigurationSaveResult> SaveAsync(
        TransportRuntimeConfiguration configuration,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_sync)
        {
            _configurations.TryGetValue(configuration.ConfigurationId, out var current);
            var currentVersion = current?.Version ?? 0;
            if (currentVersion != expectedVersion)
                return Task.FromResult(TransportConfigurationSaveResult.Conflict(current));

            var saved = configuration with
            {
                Version = currentVersion + 1,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _configurations[configuration.ConfigurationId] = saved;
            return Task.FromResult(TransportConfigurationSaveResult.Saved(saved));
        }
    }
}

public interface ITransportConfigurationService
{
    Task<TransportRuntimeConfiguration> GetAsync(CancellationToken cancellationToken = default);
    Task<TransportConfigurationSaveResult> SaveAndApplyAsync(
        TransportRuntimeConfiguration configuration,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default);
    void Apply(TransportRuntimeConfiguration configuration);
}

public sealed class TransportConfigurationService : ITransportConfigurationService
{
    private readonly ITransportConfigurationStore _store;
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly ITransportChargingCoordinator _charging;
    private readonly ITransportVehicleRegistry _vehicles;

    public TransportConfigurationService(
        ITransportConfigurationStore store,
        ITransportTrafficCoordinator traffic,
        ITransportChargingCoordinator charging,
        ITransportVehicleRegistry vehicles)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _charging = charging ?? throw new ArgumentNullException(nameof(charging));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
    }

    public async Task<TransportRuntimeConfiguration> GetAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
        ?? new TransportRuntimeConfiguration();

    public async Task<TransportConfigurationSaveResult> SaveAndApplyAsync(
        TransportRuntimeConfiguration configuration,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Validate(configuration);
            ValidateRuntimeConstraints(configuration);
        }
        catch (Exception ex)
        {
            return TransportConfigurationSaveResult.Failed(ex.Message);
        }

        var candidate = configuration with
        {
            UpdatedBy = updatedBy,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var result = await _store.SaveAsync(candidate, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (result.Success && result.Configuration is not null)
            Apply(result.Configuration);
        return result;
    }

    public void Apply(TransportRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);

        _charging.UpdatePolicy(configuration.ChargingPolicy);

        foreach (var resource in configuration.TrafficResources)
            _traffic.RegisterResource(resource);

        foreach (var station in configuration.ChargingStations)
            _charging.RegisterStation(station);

        foreach (var definition in configuration.Vehicles)
        {
            if (_vehicles.TryGet(definition.VehicleId, out var current) && current is not null)
            {
                _vehicles.Upsert(current with
                {
                    Kind = definition.Kind,
                    State = definition.Enabled
                        ? current.State
                        : TransportVehicleOperatingState.Maintenance,
                    IsOnline = definition.Enabled && current.IsOnline,
                    Capabilities = definition.Capabilities,
                    Version = current.Version + 1,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                continue;
            }

            if (!definition.Enabled)
                continue;

            _vehicles.Upsert(new TransportVehicleSnapshot
            {
                VehicleId = definition.VehicleId,
                Kind = definition.Kind,
                State = TransportVehicleOperatingState.Offline,
                CurrentNodeId = definition.InitialNodeId,
                IsOnline = false,
                BatteryPercent = definition.InitialBatteryPercent,
                Capabilities = definition.Capabilities,
                Version = 1,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
    }

    private void ValidateRuntimeConstraints(TransportRuntimeConfiguration configuration)
    {
        var activePlans = _charging.GetPlans().Where(x => !x.IsTerminal).ToArray();
        foreach (var station in configuration.ChargingStations.Where(x => !x.IsOnline))
        {
            if (activePlans.Any(x => string.Equals(x.StationId, station.StationId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"充电站 {station.StationId} 存在活动充电计划，不能直接停用");
        }

        var confirmedHolds = _traffic.GetHolds().Where(x => x.OccupancyConfirmed).ToArray();
        foreach (var resource in configuration.TrafficResources.Where(x => !x.Enabled))
        {
            if (confirmedHolds.Any(x => string.Equals(x.ResourceId, resource.ResourceId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"交通资源 {resource.ResourceId} 存在已确认物理占用，不能直接停用");
        }

        foreach (var definition in configuration.Vehicles.Where(x => !x.Enabled))
        {
            if (activePlans.Any(x => string.Equals(x.VehicleId, definition.VehicleId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"车辆 {definition.VehicleId} 存在活动充电计划，不能直接停用");

            if (_vehicles.TryGet(definition.VehicleId, out var current) &&
                current is not null &&
                current.ActiveTaskCount > 0)
            {
                throw new InvalidOperationException($"车辆 {definition.VehicleId} 仍有活动任务，不能直接停用");
            }
        }
    }

    private static void Validate(TransportRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.ConfigurationId))
            throw new ArgumentException("ConfigurationId 不能为空", nameof(configuration));

        ValidateUnique(configuration.TrafficResources.Select(x => x.ResourceId), "交通资源号");
        ValidateUnique(configuration.ChargingStations.Select(x => x.StationId), "充电站号");
        ValidateUnique(configuration.Vehicles.Select(x => x.VehicleId), "车辆号");
        ValidateUnique(configuration.Drivers.Select(x => x.DriverId), "驱动号");

        var policy = configuration.ChargingPolicy;
        if (policy.CriticalThresholdPercent is < 0 or > 100 ||
            policy.ChargeThresholdPercent is < 0 or > 100 ||
            policy.ResumeBatteryPercent is < 0 or > 100 ||
            policy.MinimumDispatchBatteryPercent is < 0 or > 100)
        {
            throw new ArgumentException("充电策略电量阈值必须在 0 到 100 之间");
        }
        if (policy.CriticalThresholdPercent > policy.ChargeThresholdPercent)
            throw new ArgumentException("临界电量不能高于充电触发阈值");
        if (policy.MinimumDispatchBatteryPercent > policy.ResumeBatteryPercent)
            throw new ArgumentException("最低派单电量不能高于恢复派单电量");

        foreach (var resource in configuration.TrafficResources)
        {
            if (resource.Capacity <= 0)
                throw new ArgumentException($"交通资源 {resource.ResourceId} Capacity 必须大于 0");
            if (resource.AgingIntervalSeconds <= 0)
                throw new ArgumentException($"交通资源 {resource.ResourceId} AgingIntervalSeconds 必须大于 0");
            if (resource.EdgeIds.Count == 0 || resource.EdgeIds.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"交通资源 {resource.ResourceId} 必须配置有效路段");
        }

        foreach (var station in configuration.ChargingStations)
        {
            if (station.Capacity <= 0)
                throw new ArgumentException($"充电站 {station.StationId} Capacity 必须大于 0");
            if (string.IsNullOrWhiteSpace(station.NodeId))
                throw new ArgumentException($"充电站 {station.StationId} NodeId 不能为空");
            if (station.SupportedVehicleKinds.Count == 0)
                throw new ArgumentException($"充电站 {station.StationId} 必须配置支持的车辆类型");
        }

        foreach (var vehicle in configuration.Vehicles)
        {
            if (vehicle.InitialBatteryPercent is < 0 or > 100)
                throw new ArgumentException($"车辆 {vehicle.VehicleId} 初始电量必须在 0 到 100 之间");
            if (vehicle.Enabled && string.IsNullOrWhiteSpace(vehicle.InitialNodeId))
                throw new ArgumentException($"启用车辆 {vehicle.VehicleId} InitialNodeId 不能为空");
        }

        foreach (var driver in configuration.Drivers.Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(driver.Protocol))
                throw new ArgumentException($"驱动 {driver.DriverId} Protocol 不能为空");
            if (string.IsNullOrWhiteSpace(driver.Endpoint))
                throw new ArgumentException($"驱动 {driver.DriverId} Endpoint 不能为空");
            if (driver.PollIntervalMs <= 0 || driver.CommandTimeoutMs <= 0)
                throw new ArgumentException($"驱动 {driver.DriverId} 轮询和超时时间必须大于 0");
        }
    }

    private static void ValidateUnique(IEnumerable<string> values, string fieldName)
    {
        var all = values.ToArray();
        if (all.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"{fieldName}不能为空");
        if (all.Length != all.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException($"{fieldName}存在重复值");
    }
}
