namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

public interface ITransportTelemetryOperation : IDisposable
{
    string TraceId { get; }
    void Complete(bool success, string? message = null, IReadOnlyDictionary<string, string>? tags = null);
}

public interface ITransportTelemetryService
{
    ITransportTelemetryOperation StartOperation(
        TransportTraceOperationKind kind,
        string operationName,
        string? requestId = null,
        string? vehicleId = null,
        IReadOnlyDictionary<string, string>? tags = null);

    void RecordQueueWait(double milliseconds, string? stationId = null);
    void RecordPlcResponse(double milliseconds, string vehicleId, bool success);
    void RecordConsistencyIssues(int count, TransportConsistencySeverity highestSeverity);
    IReadOnlyList<TransportTraceRecord> GetRecentTraces(int maxCount = 500);
    TransportTelemetryMetricsSnapshot GetMetricsSnapshot();
}

public sealed class TransportTelemetryService : ITransportTelemetryService, IDisposable
{
    private const int Capacity = 5000;
    private readonly ActivitySource _activitySource = new(TransportTelemetryNames.ActivitySourceName);
    private readonly Meter _meter = new(TransportTelemetryNames.MeterName);
    private readonly Counter<long> _operationCounter;
    private readonly Counter<long> _failureCounter;
    private readonly Histogram<double> _durationHistogram;
    private readonly Histogram<double> _queueWaitHistogram;
    private readonly Histogram<double> _plcResponseHistogram;
    private readonly Counter<long> _consistencyIssueCounter;
    private readonly ConcurrentQueue<TransportTraceRecord> _traces = new();
    private readonly object _metricsSync = new();
    private readonly Dictionary<TransportTraceOperationKind, MutableOperationMetric> _metrics = new();
    private long _consistencyIssueCount;
    private double _lastQueueWaitMilliseconds;
    private double _lastPlcResponseMilliseconds;

    public TransportTelemetryService()
    {
        _operationCounter = _meter.CreateCounter<long>(
            "wcs.transport.operations",
            unit: "operations",
            description: "EMS/RGV transport operations");
        _failureCounter = _meter.CreateCounter<long>(
            "wcs.transport.operation.failures",
            unit: "failures",
            description: "Failed EMS/RGV transport operations");
        _durationHistogram = _meter.CreateHistogram<double>(
            "wcs.transport.operation.duration",
            unit: "ms",
            description: "EMS/RGV transport operation duration");
        _queueWaitHistogram = _meter.CreateHistogram<double>(
            "wcs.transport.queue.wait",
            unit: "ms",
            description: "Production transport queue wait time");
        _plcResponseHistogram = _meter.CreateHistogram<double>(
            "wcs.transport.plc.response",
            unit: "ms",
            description: "PLC command response time");
        _consistencyIssueCounter = _meter.CreateCounter<long>(
            "wcs.transport.consistency.issues",
            unit: "issues",
            description: "Runtime, database and PLC consistency issues");
    }

    public ITransportTelemetryOperation StartOperation(
        TransportTraceOperationKind kind,
        string operationName,
        string? requestId = null,
        string? vehicleId = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(operationName))
            throw new ArgumentException("OperationName 不能为空", nameof(operationName));

        var activity = _activitySource.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag("wcs.transport.operation.kind", kind.ToString());
        activity?.SetTag("wcs.transport.request.id", requestId);
        activity?.SetTag("wcs.transport.vehicle.id", vehicleId);
        if (tags is not null)
        {
            foreach (var pair in tags)
                activity?.SetTag(pair.Key, pair.Value);
        }

        return new TransportTelemetryOperation(
            this,
            activity,
            kind,
            operationName,
            requestId,
            vehicleId,
            tags);
    }

    public void RecordQueueWait(double milliseconds, string? stationId = null)
    {
        milliseconds = Math.Max(0, milliseconds);
        Volatile.Write(ref _lastQueueWaitMilliseconds, milliseconds);
        _queueWaitHistogram.Record(
            milliseconds,
            new KeyValuePair<string, object?>("station.id", stationId ?? string.Empty));
    }

    public void RecordPlcResponse(double milliseconds, string vehicleId, bool success)
    {
        milliseconds = Math.Max(0, milliseconds);
        Volatile.Write(ref _lastPlcResponseMilliseconds, milliseconds);
        _plcResponseHistogram.Record(
            milliseconds,
            new KeyValuePair<string, object?>("vehicle.id", vehicleId),
            new KeyValuePair<string, object?>("success", success));
    }

    public void RecordConsistencyIssues(int count, TransportConsistencySeverity highestSeverity)
    {
        if (count <= 0)
            return;
        Interlocked.Add(ref _consistencyIssueCount, count);
        _consistencyIssueCounter.Add(
            count,
            new KeyValuePair<string, object?>("severity", highestSeverity.ToString()));
    }

    public IReadOnlyList<TransportTraceRecord> GetRecentTraces(int maxCount = 500) =>
        _traces
            .OrderByDescending(x => x.CompletedAtUtc)
            .Take(Math.Clamp(maxCount, 1, Capacity))
            .ToArray();

    public TransportTelemetryMetricsSnapshot GetMetricsSnapshot()
    {
        TransportOperationMetric[] operations;
        lock (_metricsSync)
        {
            operations = _metrics
                .OrderBy(x => x.Key)
                .Select(x => new TransportOperationMetric
                {
                    Kind = x.Key,
                    TotalCount = x.Value.TotalCount,
                    FailureCount = x.Value.FailureCount,
                    AverageDurationMilliseconds = x.Value.TotalCount == 0
                        ? 0
                        : Math.Round(x.Value.TotalDurationMilliseconds / x.Value.TotalCount, 2),
                    MaximumDurationMilliseconds = Math.Round(x.Value.MaximumDurationMilliseconds, 2)
                })
                .ToArray();
        }

        return new TransportTelemetryMetricsSnapshot
        {
            Operations = operations,
            ConsistencyIssueCount = Interlocked.Read(ref _consistencyIssueCount),
            LastQueueWaitMilliseconds = Math.Round(Volatile.Read(ref _lastQueueWaitMilliseconds), 2),
            LastPlcResponseMilliseconds = Math.Round(Volatile.Read(ref _lastPlcResponseMilliseconds), 2)
        };
    }

    internal void Complete(
        Activity? activity,
        TransportTraceOperationKind kind,
        string operationName,
        string? requestId,
        string? vehicleId,
        DateTime startedAtUtc,
        long elapsedTicks,
        bool success,
        string? message,
        IReadOnlyDictionary<string, string>? initialTags,
        IReadOnlyDictionary<string, string>? completionTags)
    {
        var durationMilliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;
        activity?.SetTag("wcs.transport.success", success);
        activity?.SetTag("wcs.transport.duration.ms", durationMilliseconds);
        if (!string.IsNullOrWhiteSpace(message))
            activity?.SetTag("wcs.transport.message", message);
        if (completionTags is not null)
        {
            foreach (var pair in completionTags)
                activity?.SetTag(pair.Key, pair.Value);
        }
        activity?.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, message);

        var metricTags = new TagList
        {
            { "operation.kind", kind.ToString() },
            { "operation.name", operationName },
            { "success", success }
        };
        _operationCounter.Add(1, metricTags);
        _durationHistogram.Record(durationMilliseconds, metricTags);
        if (!success)
            _failureCounter.Add(1, metricTags);

        lock (_metricsSync)
        {
            if (!_metrics.TryGetValue(kind, out var metric))
            {
                metric = new MutableOperationMetric();
                _metrics[kind] = metric;
            }
            metric.TotalCount++;
            if (!success)
                metric.FailureCount++;
            metric.TotalDurationMilliseconds += durationMilliseconds;
            metric.MaximumDurationMilliseconds = Math.Max(metric.MaximumDurationMilliseconds, durationMilliseconds);
        }

        var allTags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (initialTags is not null)
        {
            foreach (var pair in initialTags)
                allTags[pair.Key] = pair.Value;
        }
        if (completionTags is not null)
        {
            foreach (var pair in completionTags)
                allTags[pair.Key] = pair.Value;
        }

        var current = activity ?? Activity.Current;
        _traces.Enqueue(new TransportTraceRecord
        {
            TraceId = current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            SpanId = current?.SpanId.ToString() ?? Guid.NewGuid().ToString("N")[..16],
            ParentSpanId = current?.ParentSpanId.ToString(),
            Kind = kind,
            OperationName = operationName,
            RequestId = requestId,
            VehicleId = vehicleId,
            Success = success,
            DurationMilliseconds = Math.Round(durationMilliseconds, 2),
            Message = message,
            Tags = allTags,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = DateTime.UtcNow
        });
        while (_traces.Count > Capacity && _traces.TryDequeue(out _))
        {
        }

        activity?.Stop();
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }

    private sealed class MutableOperationMetric
    {
        public long TotalCount;
        public long FailureCount;
        public double TotalDurationMilliseconds;
        public double MaximumDurationMilliseconds;
    }

    private sealed class TransportTelemetryOperation : ITransportTelemetryOperation
    {
        private readonly TransportTelemetryService _owner;
        private readonly Activity? _activity;
        private readonly TransportTraceOperationKind _kind;
        private readonly string _operationName;
        private readonly string? _requestId;
        private readonly string? _vehicleId;
        private readonly DateTime _startedAtUtc = DateTime.UtcNow;
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
        private readonly IReadOnlyDictionary<string, string>? _tags;
        private int _completed;

        public TransportTelemetryOperation(
            TransportTelemetryService owner,
            Activity? activity,
            TransportTraceOperationKind kind,
            string operationName,
            string? requestId,
            string? vehicleId,
            IReadOnlyDictionary<string, string>? tags)
        {
            _owner = owner;
            _activity = activity;
            _kind = kind;
            _operationName = operationName;
            _requestId = requestId;
            _vehicleId = vehicleId;
            _tags = tags;
        }

        public string TraceId =>
            _activity?.TraceId.ToString() ?? Activity.Current?.TraceId.ToString() ?? string.Empty;

        public void Complete(
            bool success,
            string? message = null,
            IReadOnlyDictionary<string, string>? tags = null)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;
            _owner.Complete(
                _activity,
                _kind,
                _operationName,
                _requestId,
                _vehicleId,
                _startedAtUtc,
                Stopwatch.GetTimestamp() - _startedTimestamp,
                success,
                message,
                _tags,
                tags);
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _completed) == 0)
                Complete(false, "操作未显式完成");
        }
    }
}
