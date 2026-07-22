namespace Wcs.Core.TransportScheduling;

using System.Diagnostics;

/// <summary>
/// 在不改变真实/模拟 PLC 访问器行为的前提下，记录连接检查、批量读取和批量写入耗时。
/// 仅保留有限数量的内存环形记录，避免联调日志反向拖慢 200ms 轮询。
/// </summary>
public sealed class TransportObservedPlcAccessor : ITransportPlcAccessor
{
    private readonly HybridTransportPlcAccessor _inner;
    private readonly ITransportCommunicationTraceStore _traces;
    private readonly ITransportPlcSignalMapRegistry _maps;

    public TransportObservedPlcAccessor(
        HybridTransportPlcAccessor inner,
        ITransportCommunicationTraceStore traces,
        ITransportPlcSignalMapRegistry maps)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _traces = traces ?? throw new ArgumentNullException(nameof(traces));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
    }

    public async Task<bool> IsConnectedAsync(
        string driverId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var connected = await _inner.IsConnectedAsync(driverId, cancellationToken).ConfigureAwait(false);
            Append(driverId, TransportCommunicationOperation.ConnectionCheck, Array.Empty<string>(), true, started, null);
            return connected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Append(driverId, TransportCommunicationOperation.ConnectionCheck, Array.Empty<string>(), false, started, ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> ReadBatchAsync(
        string driverId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        var normalized = tags
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _inner.ReadBatchAsync(driverId, normalized, cancellationToken).ConfigureAwait(false);
            Append(driverId, TransportCommunicationOperation.BatchRead, normalized, true, started, null);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Append(driverId, TransportCommunicationOperation.BatchRead, normalized, false, started, ex.Message);
            throw;
        }
    }

    public async Task WriteBatchAsync(
        string driverId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        var tags = values.Keys
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _inner.WriteBatchAsync(driverId, values, cancellationToken).ConfigureAwait(false);
            Append(driverId, TransportCommunicationOperation.BatchWrite, tags, true, started, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Append(driverId, TransportCommunicationOperation.BatchWrite, tags, false, started, ex.Message);
            throw;
        }
    }

    private void Append(
        string driverId,
        TransportCommunicationOperation operation,
        IReadOnlyList<string> tags,
        bool success,
        long started,
        string? error)
    {
        var vehicleId = _maps.GetAll()
            .FirstOrDefault(x => string.Equals(x.DriverId, driverId, StringComparison.Ordinal))
            ?.VehicleId;
        _traces.Append(new TransportCommunicationTrace
        {
            DriverId = driverId,
            VehicleId = vehicleId,
            Operation = operation,
            Tags = tags,
            Success = success,
            DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Error = error
        });
    }
}
