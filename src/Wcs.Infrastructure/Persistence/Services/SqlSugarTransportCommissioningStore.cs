namespace Wcs.Infrastructure.Persistence.Services;

using SqlSugar;
using Wcs.Core.TransportScheduling;
using Wcs.Infrastructure.Persistence;

public sealed class SqlSugarTransportCommissioningStore : ITransportCommissioningStore
{
    private readonly string _connectionString;

    public SqlSugarTransportCommissioningStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task UpsertAsync(
        TransportCommissioningRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        var entity = new TransportCommissioningEntity
        {
            StateKey = Key(record.Category, record.RecordId),
            Category = (int)record.Category,
            RecordId = record.RecordId,
            PayloadJson = record.PayloadJson,
            UpdatedAtUtc = record.UpdatedAtUtc
        };

        using var db = CreateClient();
        var exists = db.Queryable<TransportCommissioningEntity>()
            .Where(x => x.StateKey == entity.StateKey)
            .Any();
        if (exists)
        {
            db.Updateable(entity)
                .Where(x => x.StateKey == entity.StateKey)
                .ExecuteCommand();
        }
        else
        {
            db.Insertable(entity).ExecuteCommand();
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(
        TransportCommissioningRecordCategory category,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var affected = db.Deleteable<TransportCommissioningEntity>()
            .Where(x => x.StateKey == Key(category, recordId))
            .ExecuteCommand();
        return Task.FromResult(affected > 0);
    }

    public Task<IReadOnlyList<TransportCommissioningRecord>> ListAsync(
        TransportCommissioningRecordCategory category,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        IReadOnlyList<TransportCommissioningRecord> result = db
            .Queryable<TransportCommissioningEntity>()
            .Where(x => x.Category == (int)category)
            .OrderBy(x => x.UpdatedAtUtc, OrderByType.Desc)
            .ToList()
            .Select(x => new TransportCommissioningRecord
            {
                Category = (TransportCommissioningRecordCategory)x.Category,
                RecordId = x.RecordId,
                PayloadJson = x.PayloadJson,
                UpdatedAtUtc = x.UpdatedAtUtc
            })
            .ToArray();
        return Task.FromResult(result);
    }

    private static string Key(
        TransportCommissioningRecordCategory category,
        string recordId) => $"{(int)category}:{recordId}";

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}
