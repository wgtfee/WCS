namespace Wcs.Infrastructure.Telemetry;

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.Telemetry;

/// <summary>
/// PLC telemetry 接收缓冲。
/// Buffered 模式写入内存 Channel；WriteAhead 模式先把并发请求合并为本地 WAL 批次，
/// WAL 强制刷盘成功后才向生产端返回接收成功。
/// </summary>
internal sealed class PlcTelemetryBuffer : BackgroundService, IPlcTelemetrySink, IPlcTelemetryStatusProvider
{
    private sealed record WalRequest(
        PlcTelemetryPoint Point,
        TaskCompletionSource<bool> Completion);

    private readonly Channel<PlcTelemetryPoint> _channel;
    private readonly Channel<WalRequest> _walIngress;
    private readonly FilePlcTelemetrySpool _spool;
    private readonly PlcTelemetryOptions _options;
    private readonly ILogger<PlcTelemetryBuffer> _logger;

    private long _accepted;
    private long _persisted;
    private long _replayed;
    private long _spooled;
    private long _dropped;
    private long _failedBatches;
    private long _queueDepth;
    private long _walPending;
    private long _inFlight;
    private long _lastWriteTicks;
    private string? _lastError;

    public PlcTelemetryBuffer(
        PlcTelemetryOptions options,
        FilePlcTelemetrySpool spool,
        ILogger<PlcTelemetryBuffer> logger)
    {
        _options = options;
        _spool = spool;
        _logger = logger;

        var capacity = Math.Max(options.BatchSize, options.ChannelCapacity);
        _channel = Channel.CreateBounded<PlcTelemetryPoint>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _walIngress = Channel.CreateBounded<WalRequest>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        // 上次进程留下的 spool/WAL 文件均代表曾经已经确认接收的数据。
        var recovered = spool.PendingPoints;
        _accepted = recovered;
        _spooled = recovered;
    }

    internal ChannelReader<PlcTelemetryPoint> Reader => _channel.Reader;
    internal bool UsesWriteAhead => _options.DurabilityMode == PlcTelemetryDurabilityMode.WriteAhead;

    public ValueTask<bool> EnqueueAsync(
        PlcTelemetryPoint point,
        CancellationToken cancellationToken = default)
    {
        if (_options.Provider == PlcTelemetryProvider.Disabled)
            return ValueTask.FromResult(true);

        return UsesWriteAhead
            ? EnqueueWriteAheadAsync(point, cancellationToken)
            : EnqueueBufferedAsync(point, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!UsesWriteAhead || _options.Provider == PlcTelemetryProvider.Disabled)
            return;

        _logger.LogInformation(
            "PLC telemetry write-ahead buffer started: WalBatchSize={WalBatchSize}, WalFlushIntervalMs={WalFlushIntervalMs}, Directory={Directory}",
            _options.WalBatchSize,
            _options.WalFlushIntervalMs,
            Path.GetFullPath(_options.SpoolDirectory));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await ReadWalBatchAsync(stoppingToken);
                if (batch.Count > 0)
                    await CommitWalBatchAsync(batch);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // StopAsync 会关闭写端，finally 中把已进入 ingress 的请求全部刷入 WAL。
        }
        finally
        {
            await FlushRemainingWalRequestsAsync();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (UsesWriteAhead)
            _walIngress.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private async ValueTask<bool> EnqueueBufferedAsync(
        PlcTelemetryPoint point,
        CancellationToken cancellationToken)
    {
        // 先计入守恒式，读线程即使立即取走也不会造成负数窗口。
        Interlocked.Increment(ref _accepted);
        Interlocked.Increment(ref _queueDepth);
        try
        {
            await _channel.Writer.WriteAsync(point, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _accepted);
            Interlocked.Decrement(ref _queueDepth);
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _accepted);
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref _dropped);
            Volatile.Write(ref _lastError, ex.Message);
            return false;
        }
    }

    private async ValueTask<bool> EnqueueWriteAheadAsync(
        PlcTelemetryPoint point,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new WalRequest(point, completion);

        Interlocked.Increment(ref _walPending);
        try
        {
            await _walIngress.Writer.WriteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Decrement(ref _walPending);
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _walPending);
            Interlocked.Increment(ref _dropped);
            Volatile.Write(ref _lastError, ex.Message);
            return false;
        }

        // 请求一旦进入 WAL ingress，就必须等到落盘成功或明确失败，不能被调用方取消窗口截断。
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task<List<WalRequest>> ReadWalBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<WalRequest>(Math.Max(1, _options.WalBatchSize));
        if (!await _walIngress.Reader.WaitToReadAsync(cancellationToken))
            return batch;

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, _options.WalFlushIntervalMs));
        while (batch.Count < _options.WalBatchSize)
        {
            while (batch.Count < _options.WalBatchSize && _walIngress.Reader.TryRead(out var request))
                batch.Add(request);

            if (batch.Count >= _options.WalBatchSize) break;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var waitToRead = _walIngress.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var delay = Task.Delay(remaining, cancellationToken);
            if (await Task.WhenAny(waitToRead, delay) == delay) break;
            if (!await waitToRead) break;
        }

        return batch;
    }

    private async Task CommitWalBatchAsync(IReadOnlyList<WalRequest> batch)
    {
        try
        {
            var points = batch.Select(static item => item.Point).ToArray();
            await _spool.AppendAsync(points, CancellationToken.None);

            Interlocked.Add(ref _accepted, batch.Count);
            Interlocked.Add(ref _spooled, batch.Count);
            Interlocked.Add(ref _walPending, -batch.Count);
            Volatile.Write(ref _lastError, null);
            foreach (var request in batch)
                request.Completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedBatches);
            Interlocked.Add(ref _dropped, batch.Count);
            Interlocked.Add(ref _walPending, -batch.Count);
            Volatile.Write(ref _lastError, ex.Message);
            _logger.LogCritical(
                ex,
                "PLC telemetry WAL batch could not be committed; Count={Count}",
                batch.Count);
            foreach (var request in batch)
                request.Completion.TrySetResult(false);
        }
    }

    private async Task FlushRemainingWalRequestsAsync()
    {
        var batch = new List<WalRequest>(Math.Max(1, _options.WalBatchSize));
        while (_walIngress.Reader.TryRead(out var request))
        {
            batch.Add(request);
            if (batch.Count < _options.WalBatchSize) continue;

            await CommitWalBatchAsync(batch);
            batch = new List<WalRequest>(Math.Max(1, _options.WalBatchSize));
        }

        if (batch.Count > 0)
            await CommitWalBatchAsync(batch);
    }

    internal bool TryRead(out PlcTelemetryPoint point)
    {
        if (_channel.Reader.TryRead(out point!))
        {
            Interlocked.Decrement(ref _queueDepth);
            return true;
        }
        return false;
    }

    internal void BeginChannelWrite(int count) => Interlocked.Add(ref _inFlight, count);

    internal void CompleteChannelWrite(int count)
    {
        Interlocked.Add(ref _persisted, count);
        Interlocked.Add(ref _inFlight, -count);
        Volatile.Write(ref _lastWriteTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _lastError, null);
    }

    internal void ChannelWriteSpooled(int count)
    {
        Interlocked.Add(ref _spooled, count);
        Interlocked.Add(ref _inFlight, -count);
    }

    internal void CompleteReplay(int count)
    {
        Interlocked.Add(ref _persisted, count);
        Interlocked.Add(ref _replayed, count);
        Volatile.Write(ref _lastWriteTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _lastError, null);
    }

    internal void MarkFailed(Exception exception)
    {
        Interlocked.Increment(ref _failedBatches);
        Volatile.Write(ref _lastError, exception.Message);
    }

    internal void MarkDropped(int count, Exception exception)
    {
        Interlocked.Add(ref _dropped, count);
        Interlocked.Add(ref _inFlight, -count);
        Volatile.Write(ref _lastError, exception.Message);
    }

    public PlcTelemetryStatus GetStatus()
    {
        var accepted = Interlocked.Read(ref _accepted);
        var persisted = Interlocked.Read(ref _persisted);
        var dropped = Interlocked.Read(ref _dropped);
        var bufferedQueueDepth = Math.Max(0, Interlocked.Read(ref _queueDepth));
        var walPending = Math.Max(0, Interlocked.Read(ref _walPending));
        var spoolPending = Math.Max(0, _spool.PendingPoints);
        var inFlight = Math.Max(0, Interlocked.Read(ref _inFlight));
        var lastWriteTicks = Volatile.Read(ref _lastWriteTicks);
        var conservationDelta = UsesWriteAhead
            ? accepted - persisted - dropped - spoolPending - inFlight
            : accepted - persisted - dropped - bufferedQueueDepth - spoolPending - inFlight;

        return new PlcTelemetryStatus
        {
            Provider = _options.Provider.ToString(),
            DurabilityMode = _options.DurabilityMode.ToString(),
            Accepted = accepted,
            Persisted = persisted,
            Replayed = Interlocked.Read(ref _replayed),
            Spooled = Interlocked.Read(ref _spooled),
            Dropped = dropped,
            FailedBatches = Interlocked.Read(ref _failedBatches),
            QueueDepth = UsesWriteAhead ? walPending : bufferedQueueDepth,
            WalPending = walPending,
            SpoolPending = spoolPending,
            InFlight = inFlight,
            ConservationDelta = conservationDelta,
            LastWriteUtc = lastWriteTicks == 0 ? null : new DateTime(lastWriteTicks, DateTimeKind.Utc),
            LastError = Volatile.Read(ref _lastError)
        };
    }
}
