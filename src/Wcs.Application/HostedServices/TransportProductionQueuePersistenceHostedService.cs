namespace Wcs.Application.HostedServices;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.TransportScheduling;

/// <summary>
/// 将生产竞争队列复用 TransportJournal 持久化。
/// 重启时只恢复尚未派单的等待任务；Assigned 任务由执行状态恢复，禁止重复派车。
/// </summary>
public sealed class TransportProductionQueuePersistenceHostedService : BackgroundService
{
    private readonly ITransportProductionDispatchService _production;
    private readonly ITransportJournalStore _journal;
    private readonly ILogger<TransportProductionQueuePersistenceHostedService> _logger;
    private readonly ConcurrentDictionary<string, TransportProductionQueueItem> _known = new(StringComparer.Ordinal);

    public TransportProductionQueuePersistenceHostedService(
        ITransportProductionDispatchService production,
        ITransportJournalStore journal,
        ILogger<TransportProductionQueuePersistenceHostedService> logger)
    {
        _production = production;
        _journal = journal;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RestoreAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PersistSnapshotAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EMS/RGV 生产竞争队列快照保存失败，本周期已跳过");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ProductionQueue,
            10000,
            cancellationToken).ConfigureAwait(false);
        var restoredCount = 0;
        foreach (var record in records)
        {
            TransportProductionQueueItem? item;
            try
            {
                item = JsonSerializer.Deserialize<TransportProductionQueueItem>(record.PayloadJson);
            }
            catch (JsonException)
            {
                continue;
            }

            if (item is null || string.IsNullOrWhiteSpace(item.ProductionRequest.Request.RequestId))
                continue;
            var requestId = item.ProductionRequest.Request.RequestId;
            _known[requestId] = item;
            if (!IsRestorable(item.State))
                continue;

            _production.Enqueue(item.ProductionRequest);
            restoredCount++;
        }

        _logger.LogInformation(
            "EMS/RGV 生产竞争队列恢复完成，恢复等待任务 {RestoredCount} 项；已派单任务交由执行恢复处理",
            restoredCount);
    }

    private async Task PersistSnapshotAsync(CancellationToken cancellationToken)
    {
        var current = _production.GetQueue();
        var currentIds = current
            .Select(x => x.ProductionRequest.Request.RequestId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in current)
        {
            var requestId = item.ProductionRequest.Request.RequestId;
            _known[requestId] = item;
            await SaveAsync(item, cancellationToken).ConfigureAwait(false);
        }

        foreach (var pair in _known.ToArray())
        {
            if (currentIds.Contains(pair.Key) || pair.Value.State == TransportProductionQueueState.Cancelled)
                continue;

            var tombstone = pair.Value with
            {
                State = TransportProductionQueueState.Cancelled,
                LastReason = "任务已完成、取消或转由执行状态恢复",
                UpdatedAtUtc = DateTime.UtcNow
            };
            _known[pair.Key] = tombstone;
            await SaveAsync(tombstone, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SaveAsync(
        TransportProductionQueueItem item,
        CancellationToken cancellationToken) =>
        _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ProductionQueue,
            RecordId = item.ProductionRequest.Request.RequestId,
            PayloadJson = JsonSerializer.Serialize(item),
            OccurredAtUtc = item.UpdatedAtUtc
        }, cancellationToken);

    private static bool IsRestorable(TransportProductionQueueState state) => state is
        TransportProductionQueueState.Queued or
        TransportProductionQueueState.WaitingForStation or
        TransportProductionQueueState.WaitingForTraffic or
        TransportProductionQueueState.WaitingForVehicle;
}
