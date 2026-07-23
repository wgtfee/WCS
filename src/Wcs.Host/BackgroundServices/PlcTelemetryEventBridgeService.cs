namespace Wcs.Host.BackgroundServices;

using System.Globalization;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.Telemetry;

/// <summary>把 RawSignalEvent 转换为数据库无关的 PLC 时序点。</summary>
public sealed class PlcTelemetryEventBridgeService : BackgroundService
{
    private static long _sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000;
    private static long _lastTimestampNanoseconds =
        (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    private readonly IEventBus _eventBus;
    private readonly IPlcTelemetrySink _sink;
    private readonly PlcTelemetryOptions _options;
    private readonly ILogger<PlcTelemetryEventBridgeService> _logger;

    public PlcTelemetryEventBridgeService(
        IEventBus eventBus,
        IPlcTelemetrySink sink,
        PlcTelemetryOptions options,
        ILogger<PlcTelemetryEventBridgeService> logger)
    {
        _eventBus = eventBus;
        _sink = sink;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Provider == PlcTelemetryProvider.Disabled)
        {
            _logger.LogInformation("PLC telemetry event bridge disabled");
            return Task.CompletedTask;
        }

        _eventBus.Subscribe<RawSignalEvent>(async (evt, cancellationToken) =>
        {
            var point = CreatePoint(evt);
            var accepted = await _sink.EnqueueAsync(point, cancellationToken);
            if (!accepted)
                _logger.LogError("PLC telemetry point dropped: EventId={EventId}, Signal={Signal}", evt.EventId, evt.FieldName);
        });

        _logger.LogInformation("PLC telemetry event bridge started: Provider={Provider}", _options.Provider);
        return Task.CompletedTask;
    }

    private PlcTelemetryPoint CreatePoint(RawSignalEvent evt)
    {
        var valueKind = PlcTelemetryValueKind.Text;
        bool? boolValue = null;
        double? numericValue = null;
        string? textValue = evt.NewValue;

        if (bool.TryParse(evt.NewValue, out var parsedBool))
        {
            valueKind = PlcTelemetryValueKind.Boolean;
            boolValue = parsedBool;
            textValue = null;
        }
        else if (double.TryParse(
            evt.NewValue,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out var parsedNumber))
        {
            valueKind = PlcTelemetryValueKind.Numeric;
            numericValue = parsedNumber;
            textValue = null;
        }

        var timestampUtc = evt.OccurTime.Kind == DateTimeKind.Utc
            ? evt.OccurTime
            : evt.OccurTime.ToUniversalTime();

        return new PlcTelemetryPoint
        {
            Sequence = Interlocked.Increment(ref _sequence),
            TimestampUnixNanoseconds = NextTimestampNanoseconds(timestampUtc),
            TimestampUtc = timestampUtc,
            EventId = evt.EventId,
            Site = _options.Site,
            PlcName = evt.PlcName,
            DbBlock = evt.DbBlock,
            DeviceId = ExtractDeviceId(evt.FieldName) ?? evt.FieldName,
            SignalName = evt.FieldName,
            OldValue = evt.OldValue,
            NewValue = evt.NewValue,
            ValueKind = valueKind,
            BoolValue = boolValue,
            NumericValue = numericValue,
            TextValue = textValue,
            Quality = 1,
            ValidatorPassed = evt.ValidatorPassed,
            ValidatorReason = evt.ValidatorReason,
            DomainEventType = evt.DomainEventType,
            Source = evt.Source
        };
    }

    private static long NextTimestampNanoseconds(DateTime timestampUtc)
    {
        var candidate = checked((timestampUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100);
        while (true)
        {
            var current = Volatile.Read(ref _lastTimestampNanoseconds);
            var next = Math.Max(candidate, current + 1);
            if (Interlocked.CompareExchange(ref _lastTimestampNanoseconds, next, current) == current)
                return next;
        }
    }

    private static string? ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return null;
        var parts = fieldName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : null;
    }
}
