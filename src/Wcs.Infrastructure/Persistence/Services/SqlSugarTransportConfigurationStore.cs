namespace Wcs.Infrastructure.Persistence.Services;

using System.Text.Json;
using SqlSugar;
using Wcs.Core.TransportScheduling;

public sealed class SqlSugarTransportConfigurationStore : ITransportConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqlSugarTransportConfigurationStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task<TransportRuntimeConfiguration?> LoadAsync(
        string configurationId = "runtime",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var entity = db.Queryable<TransportConfigurationEntity>()
            .Where(x => x.ConfigurationId == configurationId)
            .First();

        if (entity is null)
            return Task.FromResult<TransportRuntimeConfiguration?>(null);

        var configuration = JsonSerializer.Deserialize<TransportRuntimeConfiguration>(entity.PayloadJson, JsonOptions);
        return Task.FromResult(configuration is null
            ? null
            : configuration with
            {
                ConfigurationId = entity.ConfigurationId,
                Version = entity.Version,
                UpdatedBy = entity.UpdatedBy ?? string.Empty,
                UpdatedAtUtc = entity.UpdatedAtUtc
            });
    }

    public Task<TransportConfigurationSaveResult> SaveAsync(
        TransportRuntimeConfiguration configuration,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(configuration);

        using var db = CreateClient();
        var current = db.Queryable<TransportConfigurationEntity>()
            .Where(x => x.ConfigurationId == configuration.ConfigurationId)
            .First();

        var currentVersion = current?.Version ?? 0;
        if (currentVersion != expectedVersion)
            return Task.FromResult(TransportConfigurationSaveResult.Conflict(ToConfiguration(current)));

        var saved = configuration with
        {
            Version = currentVersion + 1,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var entity = new TransportConfigurationEntity
        {
            ConfigurationId = saved.ConfigurationId,
            Version = saved.Version,
            PayloadJson = JsonSerializer.Serialize(saved, JsonOptions),
            UpdatedBy = saved.UpdatedBy,
            UpdatedAtUtc = saved.UpdatedAtUtc
        };

        try
        {
            if (current is null)
            {
                db.Insertable(entity).ExecuteCommand();
                return Task.FromResult(TransportConfigurationSaveResult.Saved(saved));
            }

            var affected = db.Updateable(entity)
                .Where(x => x.ConfigurationId == configuration.ConfigurationId && x.Version == expectedVersion)
                .ExecuteCommand();

            if (affected == 0)
            {
                var latest = db.Queryable<TransportConfigurationEntity>()
                    .Where(x => x.ConfigurationId == configuration.ConfigurationId)
                    .First();
                return Task.FromResult(TransportConfigurationSaveResult.Conflict(ToConfiguration(latest)));
            }

            return Task.FromResult(TransportConfigurationSaveResult.Saved(saved));
        }
        catch (Exception ex)
        {
            return Task.FromResult(TransportConfigurationSaveResult.Failed(ex.Message));
        }
    }

    private TransportRuntimeConfiguration? ToConfiguration(TransportConfigurationEntity? entity)
    {
        if (entity is null)
            return null;
        var configuration = JsonSerializer.Deserialize<TransportRuntimeConfiguration>(entity.PayloadJson, JsonOptions);
        return configuration is null
            ? null
            : configuration with
            {
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
