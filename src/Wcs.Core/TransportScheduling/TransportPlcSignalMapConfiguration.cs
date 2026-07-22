namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public sealed record TransportPlcSignalMapSaveResult
{
    public bool Success { get; init; }
    public bool VersionConflict { get; init; }
    public TransportPlcSignalMap? Map { get; init; }
    public string? Error { get; init; }

    public static TransportPlcSignalMapSaveResult Saved(TransportPlcSignalMap map) =>
        new() { Success = true, Map = map };
    public static TransportPlcSignalMapSaveResult Conflict(TransportPlcSignalMap? current) =>
        new() { VersionConflict = true, Map = current, Error = "点位映射版本已变化，请刷新后重试" };
    public static TransportPlcSignalMapSaveResult Failed(string error) => new() { Error = error };
}

public interface ITransportPlcSignalMapStore
{
    Task<IReadOnlyList<TransportPlcSignalMap>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task<TransportPlcSignalMap?> GetAsync(string vehicleId, CancellationToken cancellationToken = default);
    Task<TransportPlcSignalMapSaveResult> SaveAsync(
        TransportPlcSignalMap map,
        long expectedVersion,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string vehicleId, long expectedVersion, CancellationToken cancellationToken = default);
}

public sealed class InMemoryTransportPlcSignalMapStore : ITransportPlcSignalMapStore
{
    private readonly ConcurrentDictionary<string, TransportPlcSignalMap> _maps = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<IReadOnlyList<TransportPlcSignalMap>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TransportPlcSignalMap> result = _maps.Values
            .OrderBy(x => x.VehicleId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<TransportPlcSignalMap?> GetAsync(string vehicleId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _maps.TryGetValue(vehicleId, out var map);
        return Task.FromResult(map);
    }

    public Task<TransportPlcSignalMapSaveResult> SaveAsync(
        TransportPlcSignalMap map,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _maps.TryGetValue(map.VehicleId, out var current);
            if ((current?.Version ?? 0) != expectedVersion)
                return Task.FromResult(TransportPlcSignalMapSaveResult.Conflict(current));

            var saved = map with
            {
                Version = expectedVersion + 1,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _maps[map.VehicleId] = saved;
            return Task.FromResult(TransportPlcSignalMapSaveResult.Saved(saved));
        }
    }

    public Task<bool> DeleteAsync(
        string vehicleId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_maps.TryGetValue(vehicleId, out var current) || current.Version != expectedVersion)
                return Task.FromResult(false);
            return Task.FromResult(_maps.TryRemove(vehicleId, out _));
        }
    }
}

public interface ITransportPlcSignalMapService
{
    Task<IReadOnlyList<TransportPlcSignalMap>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TransportPlcSignalMapSaveResult> SaveAndApplyAsync(
        TransportPlcSignalMap map,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteAndApplyAsync(
        string vehicleId,
        long expectedVersion,
        CancellationToken cancellationToken = default);
    Task LoadAndApplyAsync(CancellationToken cancellationToken = default);
}

public sealed class TransportPlcSignalMapService : ITransportPlcSignalMapService
{
    private readonly ITransportPlcSignalMapStore _store;
    private readonly ITransportPlcSignalMapRegistry _registry;
    private readonly ITransportConfigurationService? _configuration;

    public TransportPlcSignalMapService(
        ITransportPlcSignalMapStore store,
        ITransportPlcSignalMapRegistry registry,
        ITransportConfigurationService? configuration = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configuration = configuration;
    }

    public Task<IReadOnlyList<TransportPlcSignalMap>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        _store.LoadAllAsync(cancellationToken);

    public async Task<TransportPlcSignalMapSaveResult> SaveAndApplyAsync(
        TransportPlcSignalMap map,
        long expectedVersion,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ValidateAsync(map, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return TransportPlcSignalMapSaveResult.Failed(ex.Message);
        }

        var candidate = map with
        {
            UpdatedBy = updatedBy,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var result = await _store.SaveAsync(candidate, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (result.Success && result.Map is not null)
            _registry.Upsert(result.Map);
        return result;
    }

    public async Task<bool> DeleteAndApplyAsync(
        string vehicleId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _store.DeleteAsync(vehicleId, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (deleted)
            _registry.Remove(vehicleId);
        return deleted;
    }

    public async Task LoadAndApplyAsync(CancellationToken cancellationToken = default)
    {
        var maps = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var map in maps)
            await ValidateAsync(map, cancellationToken).ConfigureAwait(false);
        _registry.ReplaceAll(maps);
    }

    private async Task ValidateAsync(TransportPlcSignalMap map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (string.IsNullOrWhiteSpace(map.VehicleId))
            throw new ArgumentException("VehicleId 不能为空");
        if (string.IsNullOrWhiteSpace(map.DriverId))
            throw new ArgumentException("DriverId 不能为空");
        if (map.PollIntervalMs <= 0 || map.HeartbeatTimeoutMs <= 0)
            throw new ArgumentException("轮询周期和心跳超时必须大于 0");

        if (_configuration is not null)
        {
            var runtime = await _configuration.GetAsync(cancellationToken).ConfigureAwait(false);
            if (runtime.Version > 0)
            {
                var driver = runtime.Drivers.FirstOrDefault(x =>
                    string.Equals(x.DriverId, map.DriverId, StringComparison.Ordinal));
                if (driver is null)
                    throw new ArgumentException($"驱动 {map.DriverId} 不存在于运行配置");
                if (driver.Kind != map.Kind)
                    throw new ArgumentException($"驱动 {map.DriverId} 与车辆类型不匹配");
                if (!driver.Enabled && map.Enabled)
                    throw new ArgumentException($"驱动 {map.DriverId} 已停用");
            }
        }

        if (map.Mode == TransportDriverMode.Simulation)
            return;

        var required = new Dictionary<string, string>
        {
            [nameof(map.HeartbeatTag)] = map.HeartbeatTag,
            [nameof(map.CurrentNodeTag)] = map.CurrentNodeTag,
            [nameof(map.OperatingStateTag)] = map.OperatingStateTag,
            [nameof(map.StateSequenceTag)] = map.StateSequenceTag,
            [nameof(map.CommandSequenceTag)] = map.CommandSequenceTag,
            [nameof(map.CommandCodeTag)] = map.CommandCodeTag,
            [nameof(map.CommandRequestTag)] = map.CommandRequestTag,
            [nameof(map.AcknowledgedSequenceTag)] = map.AcknowledgedSequenceTag,
            [nameof(map.CommandAcceptedTag)] = map.CommandAcceptedTag,
            [nameof(map.CommandCompletedTag)] = map.CommandCompletedTag
        };
        var missing = required.Where(x => string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Key).ToArray();
        if (missing.Length > 0)
            throw new ArgumentException($"PLC 点位映射缺少必填标签：{string.Join(", ", missing)}");

        if (map.NodeCodeMap.Values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("NodeCodeMap 不能包含空节点号");
        if (map.TargetNodeCodeMap.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("TargetNodeCodeMap 不能包含空节点号");
    }
}
