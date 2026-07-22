namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public enum TransportJournalCategory
{
    ChargingPlan = 0,
    TaskReassignment = 1,
    TrafficIncident = 2,
    PerformanceSnapshot = 3,
    DriverState = 4
}

public sealed record TransportJournalRecord
{
    public string JournalId { get; init; } = Guid.NewGuid().ToString("N");
    public TransportJournalCategory Category { get; init; }
    public string RecordId { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ITransportJournalStore
{
    Task UpsertAsync(TransportJournalRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportJournalRecord>> QueryAsync(
        TransportJournalCategory? category = null,
        int maxCount = 500,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryTransportJournalStore : ITransportJournalStore
{
    private readonly ConcurrentDictionary<string, TransportJournalRecord> _records = new(StringComparer.Ordinal);

    public Task UpsertAsync(TransportJournalRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        var key = $"{(int)record.Category}:{record.RecordId}";
        _records[key] = record with { UpdatedAtUtc = DateTime.UtcNow };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransportJournalRecord>> QueryAsync(
        TransportJournalCategory? category = null,
        int maxCount = 500,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = _records.Values.AsEnumerable();
        if (category.HasValue)
            query = query.Where(x => x.Category == category.Value);

        IReadOnlyList<TransportJournalRecord> result = query
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(Math.Max(1, maxCount))
            .ToArray();
        return Task.FromResult(result);
    }
}
