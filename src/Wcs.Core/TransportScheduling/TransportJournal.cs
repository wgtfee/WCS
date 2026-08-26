namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public enum TransportJournalCategory
{
    ChargingPlan = 0,
    TaskReassignment = 1,
    TrafficIncident = 2,
    PerformanceSnapshot = 3,
    DriverState = 4,
    ProductionTuning = 5,
    ProductionStation = 6,
    SingleTrackSection = 7,
    ProductionTrend = 8,
    DispatchDecision = 9,
    ProductionQueue = 10,
    ConsistencyReport = 11,
    ConfigurationSnapshot = 12,
    ObservabilityHealth = 13,
    ProductionReadiness = 14,
    OperationalBaseline = 15,
    LogicalBackup = 16,
    RecoveryDrill = 17,
    SimulationRun = 18,
    StrategyComparison = 19,
    CapacityBenchmark = 20,
    FinalAcceptanceReport = 21,
    OptimizationRecommendation = 22
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
    /// <summary>内存日志硬上限；DispatchDecision 等 RecordId 每次都是新 GUID，必须淘汰旧记录。</summary>
    private const int MaxRecords = 20_000;

    private readonly ConcurrentDictionary<string, TransportJournalRecord> _records = new(StringComparer.Ordinal);

    public Task UpsertAsync(TransportJournalRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        var key = $"{(int)record.Category}:{record.RecordId}";
        _records[key] = record with { UpdatedAtUtc = DateTime.UtcNow };

        if (_records.Count > MaxRecords)
            EvictOldest();

        return Task.CompletedTask;
    }

    /// <summary>超过上限时按 OccurredAtUtc 淘汰最旧的 10% 记录。</summary>
    private void EvictOldest()
    {
        try
        {
            var target = MaxRecords - (MaxRecords / 10);
            var oldest = _records.Values
                .OrderBy(x => x.OccurredAtUtc)
                .Take(_records.Count - target)
                .Select(x => $"{(int)x.Category}:{x.RecordId}")
                .ToList();

            foreach (var key in oldest)
                _records.TryRemove(key, out _);
        }
        catch
        {
            // 淘汰失败不影响主写入路径
        }
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
