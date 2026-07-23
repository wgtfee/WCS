namespace Wcs.Infrastructure.Telemetry;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.Telemetry;

/// <summary>
/// 单消费者批量写入服务。失败批次先落本地 spool，连接恢复后按文件顺序重放。
/// </summary>
internal sealed class PlcTelemetryBatchWriterService : BackgroundService
{
    private readonly PlcTelemetryBuffer _buffer;
    private readonly FilePlcTelemetrySpool _spool;
    private readonly IPlcTelemetryStore _store;
    private readonly PlcTelemetryOptions _options;
    private readonly ILogger<PlcTelemetryBatchWriterService> _logger;

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
                if (await TryReplayOldestAsync(stoppingToken))
                    continue;

                var batch = await ReadChannelBatchAsync(stoppingToken);
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

    private async Task<bool> TryReplayOldestAsync(CancellationToken cancellationToken)
    {
        var spoolBatch = await _spool.TryPeekOldestAsync(cancellationToken);
        if (spoolBatch is null) return false;

        if (spoolBatch.Points.Count == 0)
        {
            await _spool.AcknowledgeAsync(spoolBatch, cancellationToken);
            return true;
        }

        try
        {
            await _store.WriteBatchAsync(spoolBatch.Points, cancellationToken);
            await _spool.AcknowledgeAsync(spoolBatch, cancellationToken);
            _buffer.CompleteReplay(spoolBatch.Points.Count);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _buffer.MarkFailed(ex);
            _logger.LogWarning(
                ex,
                "PLC telemetry spool replay failed: Provider={Provider}, Count={Count}",
                _store.ProviderName,
                spoolBatch.Points.Count);
            await Task.Delay(Math.Max(100, _options.RetryDelayMs), cancellationToken);
            return true;
        }
    }

    private async Task<List<PlcTelemetryPoint>> ReadChannelBatchAsync(
        CancellationToken cancellationToken)
    {
        var batch = new List<PlcTelemetryPoint>(Math.Max(1, _options.BatchSize));
        if (!await _buffer.Reader.WaitToReadAsync(cancellationToken))
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
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _buffer.MarkFailed(ex);
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

            _logger.LogWarning(
                ex,
                "PLC telemetry write failed and batch was moved to spool: Provider={Provider}, Count={Count}",
                _store.ProviderName,
                batch.Count);
            await Task.Delay(Math.Max(100, _options.RetryDelayMs), cancellationToken);
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
