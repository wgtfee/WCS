namespace Wcs.Infrastructure.Telemetry;

using System.Threading.Channels;
using Wcs.Core.Telemetry;

internal sealed class PlcTelemetryBuffer : IPlcTelemetrySink, IPlcTelemetryStatusProvider
{
    private readonly Channel<PlcTelemetryPoint> _channel;
    private readonly FilePlcTelemetrySpool _spool;
    private readonly PlcTelemetryOptions _options;

    private long _accepted;
    private long _persisted;
    private long _replayed;
    private long _spooled;
    private long _dropped;
    private long _failedBatches;
    private long _queueDepth;
    private long _inFlight;
    private long _lastWriteTicks;
    private string? _lastError;

    public PlcTelemetryBuffer(
        PlcTelemetryOptions options,
        FilePlcTelemetrySpool spool)
    {
        _options = options;
        _spool = spool;

        var capacity = Math.Max(options.BatchSize, options.ChannelCapacity);
        _channel = Channel.CreateBounded<PlcTelemetryPoint>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        var recovered = spool.PendingPoints;
        _accepted = recovered;
        _spooled = recovered;
    }

    internal ChannelReader<PlcTelemetryPoint> Reader => _channel.Reader;

    public async ValueTask<bool> EnqueueAsync(
        PlcTelemetryPoint point,
        CancellationToken cancellationToken = default)
    {
        if (_options.Provider == PlcTelemetryProvider.Disabled)
            return true;

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
        var queueDepth = Math.Max(0, Interlocked.Read(ref _queueDepth));
        var spoolPending = Math.Max(0, _spool.PendingPoints);
        var inFlight = Math.Max(0, Interlocked.Read(ref _inFlight));
        var lastWriteTicks = Volatile.Read(ref _lastWriteTicks);

        return new PlcTelemetryStatus
        {
            Provider = _options.Provider.ToString(),
            Accepted = accepted,
            Persisted = persisted,
            Replayed = Interlocked.Read(ref _replayed),
            Spooled = Interlocked.Read(ref _spooled),
            Dropped = dropped,
            FailedBatches = Interlocked.Read(ref _failedBatches),
            QueueDepth = queueDepth,
            SpoolPending = spoolPending,
            InFlight = inFlight,
            ConservationDelta = accepted - persisted - dropped - queueDepth - spoolPending - inFlight,
            LastWriteUtc = lastWriteTicks == 0 ? null : new DateTime(lastWriteTicks, DateTimeKind.Utc),
            LastError = Volatile.Read(ref _lastError)
        };
    }
}
