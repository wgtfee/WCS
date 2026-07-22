namespace Wcs.Infrastructure.Persistence.Services;

using System.Text.Json;
using SqlSugar;
using Wcs.Core.TransportScheduling;
using Wcs.Infrastructure.Persistence;

public sealed class SqlSugarTransportPlcSignalMapStore : ITransportPlcSignalMapStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqlSugarTransportPlcSignalMapStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task<IReadOnlyList<TransportPlcSignalMap>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        IReadOnlyList<TransportPlcSignalMap> result = db.Queryable<TransportPlcSignalMapEntity>()
            .OrderBy(x => x.VehicleId)
            .ToList()
            .Select(ToMap)
            .Where(x => x is not null)
            .Cast<TransportPlcSignalMap>()
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<TransportPlcSignalMap?> GetAsync(
        string vehicleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = db.Queryable<TransportPlcSignalMapEntity>()
            .Where(x => x.VehicleId == vehicleId)
            .First();
        return Task.FromResult(ToMap(entity));
    }

    public Task<TransportPlcSignalMapSaveResult> SaveAsync(
        TransportPlcSignalMap map,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(map);
        using var db = CreateClient();
        var current = db.Queryable<TransportPlcSignalMapEntity>()
            .Where(x => x.VehicleId == map.VehicleId)
            .First();
        if ((current?.Version ?? 0) != expectedVersion)
            return Task.FromResult(TransportPlcSignalMapSaveResult.Conflict(ToMap(current)));

        var saved = map with
        {
            Version = expectedVersion + 1,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var entity = ToEntity(saved);

        try
        {
            if (current is null)
            {
                db.Insertable(entity).ExecuteCommand();
                return Task.FromResult(TransportPlcSignalMapSaveResult.Saved(saved));
            }

            var affected = db.Updateable(entity)
                .Where(x => x.VehicleId == map.VehicleId && x.Version == expectedVersion)
                .ExecuteCommand();
            if (affected == 0)
            {
                var latest = db.Queryable<TransportPlcSignalMapEntity>()
                    .Where(x => x.VehicleId == map.VehicleId)
                    .First();
                return Task.FromResult(TransportPlcSignalMapSaveResult.Conflict(ToMap(latest)));
            }

            return Task.FromResult(TransportPlcSignalMapSaveResult.Saved(saved));
        }
        catch (Exception ex)
        {
            return Task.FromResult(TransportPlcSignalMapSaveResult.Failed(ex.Message));
        }
    }

    public Task<bool> DeleteAsync(
        string vehicleId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var affected = db.Deleteable<TransportPlcSignalMapEntity>()
            .Where(x => x.VehicleId == vehicleId && x.Version == expectedVersion)
            .ExecuteCommand();
        return Task.FromResult(affected > 0);
    }

    private static TransportPlcSignalMapEntity ToEntity(TransportPlcSignalMap map) => new()
    {
        VehicleId = map.VehicleId,
        DriverId = map.DriverId,
        VehicleKind = (int)map.Kind,
        DriverMode = (int)map.Mode,
        Enabled = map.Enabled,
        Version = map.Version,
        PayloadJson = JsonSerializer.Serialize(map, JsonOptions),
        UpdatedBy = map.UpdatedBy,
        UpdatedAtUtc = map.UpdatedAtUtc
    };

    private static TransportPlcSignalMap? ToMap(TransportPlcSignalMapEntity? entity)
    {
        if (entity is null)
            return null;
        var map = JsonSerializer.Deserialize<TransportPlcSignalMap>(entity.PayloadJson, JsonOptions);
        return map is null
            ? null
            : map with
            {
                VehicleId = entity.VehicleId,
                DriverId = entity.DriverId,
                Kind = (TransportVehicleKind)entity.VehicleKind,
                Mode = (TransportDriverMode)entity.DriverMode,
                Enabled = entity.Enabled,
                Version = entity.Version,
                UpdatedBy = entity.UpdatedBy ?? string.Empty,
                UpdatedAtUtc = entity.UpdatedAtUtc
            };
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}
