namespace Wcs.Infrastructure.Persistence.Services;

using SqlSugar;
using Wcs.Core.TransportScheduling;

public sealed class SqlSugarTransportJournalStore : ITransportJournalStore
{
    private readonly string _connectionString;

    public SqlSugarTransportJournalStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task UpsertAsync(TransportJournalRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        using var db = CreateClient();

        var key = $"{(int)record.Category}:{record.RecordId}";
        var entity = new TransportJournalEntity
        {
            JournalKey = key,
            Category = (int)record.Category,
            RecordId = record.RecordId,
            PayloadJson = record.PayloadJson,
            OccurredAtUtc = record.OccurredAtUtc,
            UpdatedAtUtc = DateTime.UtcNow
        };

        if (db.Queryable<TransportJournalEntity>().Any(x => x.JournalKey == key))
            db.Updateable(entity).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransportJournalRecord>> QueryAsync(
        TransportJournalCategory? category = null,
        int maxCount = 500,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var query = db.Queryable<TransportJournalEntity>();
        if (category.HasValue)
        {
            var value = (int)category.Value;
            query = query.Where(x => x.Category == value);
        }

        IReadOnlyList<TransportJournalRecord> result = query
            .OrderBy(x => x.OccurredAtUtc, OrderByType.Desc)
            .Take(Math.Max(1, maxCount))
            .ToList()
            .Select(x => new TransportJournalRecord
            {
                JournalId = x.JournalKey,
                Category = (TransportJournalCategory)x.Category,
                RecordId = x.RecordId,
                PayloadJson = x.PayloadJson,
                OccurredAtUtc = x.OccurredAtUtc,
                UpdatedAtUtc = x.UpdatedAtUtc
            })
            .ToArray();

        return Task.FromResult(result);
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}
