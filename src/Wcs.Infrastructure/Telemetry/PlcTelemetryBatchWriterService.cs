namespace Wcs.Infrastructure.Telemetry;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.Telemetry;

/// <summary>
/// 单消费者批量写入服务。Provider 不可用时继续把内存队列按批转入 spool，
/// 避免旧 spool 重放失败后阻塞生产端。
/// </summary>
internal sealed class PlcTelemetryBatchWriterService : BackgroundService
{
    private enum ReplayResult
    {
        None,
        Succeeded,
        Failed
    }

    private readonly PlcTelemetryBuffer _buffer;
    private readonly FilePlcTelemetrySpool _spool;
    private readonly IPlcTelemetryStore _store;
    private readonly PlcTelemetryOptions _options;
    private readonly ILogger<PlcTelemetryBatchWriterService> _logger;
    private DateTime _nextReplayAttemptUtc = DateTime.MinValue;
    private bool _providerUnavailable;

    public PlcTelemetryBatchWriterService(
        PlcTelemetryBuffer buffer,
        FilePlcTelemetrySpool spool,
        IPlcTelemetryStore store,
        PlcTelemetryOptions options,
        ILogger<PlcTelemetryBatchWriterService> logger)
    {
        _buffer = buffer;
        _spool = spool;
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Provider == PlcTelemetryProvider.Disabled)
        {
            _logger.LogInformation("PLC telemetry storage disabled");
            return;
        }

        _logger.LogInformation(
            "PLC telemetry writer started: Provider={Provider}, BatchSize={BatchSize}, FlushIntervalMs={FlushIntervalMs}, Spool={Spool}",
            _store.ProviderName,
            _options.BatchSize,
            _options.FlushIntervalMs,
            Path.GetFullPath(_options.SpoolDirectory));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_spool.PendingPoints > 0 && DateTime.UtcNow >= _nextReplayAttemptUtc)
                {
                    var replay = await TryReplayOldestAsync(stoppingToken);
                    if (replay == ReplayResult.Succeeded)
                    {
                        _providerUnavailable = false;
                        continue;
                    }
                    if (replay == ReplayResult.Failed)
                    {
                        _providerUnavailable = true;
                        _nextReplayAttemptUtc = DateTime.UtcNow.AddMilliseconds(
                            Math.Max(100, _options.RetryDelayMs));
                    }
                }

                if (_providerUnavailable || _spool.PendingPoints > 0)
                {
                    var offlineBatch = await ReadChannelBatchAsync(
                        stoppingToken,
                        Math.Min(Math.Max(50, _options.FlushIntervalMs), 500));
                    if (offlineBatch.Count > 0)
                    {
                        await SpoolChannelBatchAsync(offlineBatch, stoppingToken);
                        continue;
                    }

                    var delay = _nextReplayAttemptUtc - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds, 250)),
                            stoppingToken);
                    continue;
                }

                var batch = await ReadChannelBatchAsync(
                    stoppingToken,
                    Math.Max(50, _options.FlushIntervalMs));
                if (batch.Count == 0) continue;
                await PersistChannelBatchAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        finally
        {
            await SpoolRemainingChannelDataAsync();
        }
    }

    private async Task<ReplayResult> TryReplayOldestAsync(CancellationToken cancellationToken)
    {
        var spoolBatch = await _spool.TryPeekOldestAsync(cancellationToken);
        if (spoolBatch is null) return ReplayResult.None;

        if (spoolBatch.Points.Count == 0)
        {
            await _spool.AcknowledgeAsync(spoolBatch, cancellationToken);
            return ReplayResult.Succeeded;
        }

        try
        {
            await _store.WriteBatchAsync(spoolBatch.Points, cancellationToken);
            await _spool.AcknowledgeAsync(spoolBatch, cancellationToken);
            _buffer.CompleteReplay(spoolBatch.Points.Count);
            return ReplayResult.Succeeded;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _buffer.MarkFailed(ex);
            _logger.LogWarning(
                ex,
                "PLC telemetry spool replay failed: Provider={Provider}, Count={Count}",
                _store.ProviderName,
                spoolBatch.Points.Count);
            return ReplayResult.Failed;
        }
    }

    private async Task<List<PlcTelemetryPoint>> ReadChannelBatchAsync(
        CancellationToken cancellationToken,
        int waitForFirstMilliseconds)
    {
        var batch = new List<PlcTelemetryPoint>(Math.Max(1, _options.BatchSize));
        var readyTask = _buffer.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var firstWait = Task.Delay(Math.Max(10, waitForFirstMilliseconds), cancellationToken);
        if (await Task.WhenAny(readyTask, firstWait) == firstWait)
            return batch;
        if (!await readyTask)
            return batch;

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(10, _options.FlushIntervalMs));
        while (batch.Count < _options.BatchSize)
        {
            while (batch.Count < _options.BatchSize && _buffer.TryRead(out var point))
                batch.Add(point);

            if (batch.Count >= _options.BatchSize) break;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var waitToRead = _buffer.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var delay = Task.Delay(remaining, cancellationToken);
            if (await Task.WhenAny(waitToRead, delay) == delay) break;
            if (!await waitToRead) break;
        }

        return batch;
    }

    private async Task PersistChannelBatchAsync(
        IReadOnlyList<PlcTelemetryPoint> batch,
        CancellationToken cancellationToken)
    {
        _buffer.BeginChannelWrite(batch.Count);
        try
        {
            await _store.WriteBatchAsync(batch, cancellationToken);
            _buffer.CompleteChannelWrite(batch.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SpoolCancelledInFlightBatchAsync(batch);
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _buffer.MarkFailed(ex);
            _providerUnavailable = true;
            _nextReplayAttemptUtc = DateTime.UtcNow.AddMilliseconds(
                Math.Max(100, _options.RetryDelayMs));
            await SpoolStartedBatchAsync(batch, cancellationToken, ex);
        }
    }

    private async Task SpoolChannelBatchAsync(
        IReadOnlyCollection<PlcTelemetryPoint> batch,
        CancellationToken cancellationToken)
    {
        _buffer.BeginChannelWrite(batch.Count);
        await SpoolStartedBatchAsync(batch, cancellationToken, null);
    }

    private async Task SpoolStartedBatchAsync(
        IReadOnlyCollection<PlcTelemetryPoint> batch,
        CancellationToken cancellationToken,
        Exception? providerException)
    {
        try
        {
            await _spool.AppendAsync(batch, cancellationToken);
            _buffer.ChannelWriteSpooled(batch.Count);
        }
        catch (Exception spoolException) when (!cancellationToken.IsCancellationRequested)
        {
            _buffer.MarkDropped(batch.Count, spoolException);
            _logger.LogCritical(
                spoolException,
                "PLC telemetry batch could not be persisted or spooled; Count={Count}",
                batch.Count);
        }

        if (providerException is not null)
        {
            _logger.LogWarning(
                providerException,
                "PLC telemetry write failed and batch was moved to spool: Provider={Provider}, Count={Count}",
                _store.ProviderName,
                batch.Count);
        }
    }

    private async Task SpoolCancelledInFlightBatchAsync(IReadOnlyCollection<PlcTelemetryPoint> batch)
    {
        try
        {
            await _spool.AppendAsync(batch, CancellationToken.None);
            _buffer.ChannelWriteSpooled(batch.Count);
        }
        catch (Exception ex)
        {
            _buffer.MarkDropped(batch.Count, ex);
            _logger.LogCritical(ex, "PLC telemetry cancelled in-flight batch could not be spooled; Count={Count}", batch.Count);
        }
    }

    private async Task SpoolRemainingChannelDataAsync()
    {
        var remaining = new List<PlcTelemetryPoint>(Math.Max(1, _options.BatchSize));
        while (_buffer.TryRead(out var point))
        {
            remaining.Add(point);
            if (remaining.Count < _options.BatchSize) continue;

            await SpoolShutdownBatchAsync(remaining);
            remaining = new List<PlcTelemetryPoint>(Math.Max(1, _options.BatchSize));
        }

        if (remaining.Count > 0)
            await SpoolShutdownBatchAsync(remaining);
    }

    private async Task SpoolShutdownBatchAsync(IReadOnlyCollection<PlcTelemetryPoint> batch)
    {
        _buffer.BeginChannelWrite(batch.Count);
        try
        {
            await _spool.AppendAsync(batch, CancellationToken.None);
            _buffer.ChannelWriteSpooled(batch.Count);
        }
        catch (Exception ex)
        {
            _buffer.MarkDropped(batch.Count, ex);
            _logger.LogCritical(ex, "PLC telemetry shutdown spool failed; Count={Count}", batch.Count);
        }
    }
}
