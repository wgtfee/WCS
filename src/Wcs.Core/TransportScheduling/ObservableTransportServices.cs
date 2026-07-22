namespace Wcs.Core.TransportScheduling;

public sealed class ObservableUnifiedTransportDispatchEngine : IUnifiedTransportDispatchEngine
{
    private readonly UnifiedTransportDispatchEngine _inner;
    private readonly ITransportTelemetryService _telemetry;

    public ObservableUnifiedTransportDispatchEngine(
        UnifiedTransportDispatchEngine inner,
        ITransportTelemetryService telemetry)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public async Task<TransportDispatchResult> DispatchAsync(
        TransportDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.Dispatch,
            "transport.dispatch",
            request.RequestId,
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source.node"] = request.SourceNodeId,
                ["destination.node"] = request.DestinationNodeId,
                ["priority"] = request.Priority.ToString()
            });

        try
        {
            var result = await _inner.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            operation.Complete(
                result.Success,
                result.FailureReason,
                result.Assignment is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["vehicle.id"] = result.Assignment.VehicleId,
                        ["reservation.id"] = result.Assignment.ReservationId
                    });
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            throw;
        }
    }

    public bool TryGetAssignment(string requestId, out TransportDispatchAssignment? assignment) =>
        _inner.TryGetAssignment(requestId, out assignment);

    public bool Complete(string requestId) => _inner.Complete(requestId);
}

public sealed class ObservableTransportCommandDispatcher : ITransportCommandDispatcher
{
    private readonly TransportCommandDispatcher _inner;
    private readonly ITransportTelemetryService _telemetry;

    public ObservableTransportCommandDispatcher(
        TransportCommandDispatcher inner,
        ITransportTelemetryService telemetry)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public async Task<TransportCommandRecord> DispatchAsync(
        TransportExecutionCommand command,
        TransportVehicleKind vehicleKind,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var started = DateTime.UtcNow;
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.PlcCommand,
            "transport.plc.command",
            command.RequestId,
            command.VehicleId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command.id"] = command.CommandId,
                ["command.type"] = command.CommandType.ToString(),
                ["vehicle.kind"] = vehicleKind.ToString(),
                ["target.node"] = command.TargetNodeId ?? string.Empty
            });

        try
        {
            var record = await _inner.DispatchAsync(
                command,
                vehicleKind,
                maxRetries,
                cancellationToken).ConfigureAwait(false);
            var success = record.Status is
                TransportCommandStatus.Acknowledged or
                TransportCommandStatus.Completed;
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            _telemetry.RecordPlcResponse(elapsed, command.VehicleId, success);
            operation.Complete(
                success,
                record.Error,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["command.status"] = record.Status.ToString(),
                    ["retry.count"] = record.RetryCount.ToString()
                });
            return record;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _telemetry.RecordPlcResponse(
                (DateTime.UtcNow - started).TotalMilliseconds,
                command.VehicleId,
                false);
            operation.Complete(false, ex.Message);
            throw;
        }
    }
}

public sealed class ObservableTransportProductionDispatchService : ITransportProductionDispatchService
{
    private readonly ReliableTransportProductionDispatchService _inner;
    private readonly ITransportTelemetryService _telemetry;

    public ObservableTransportProductionDispatchService(
        ReliableTransportProductionDispatchService inner,
        ITransportTelemetryService telemetry)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public TransportProductionQueueItem Enqueue(TransportProductionDispatchRequest request) =>
        _inner.Enqueue(request);

    public bool Cancel(string requestId) => _inner.Cancel(requestId);

    public bool Complete(string requestId) => _inner.Complete(requestId);

    public async Task<TransportProductionDispatchCycleResult> DispatchCycleAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.DispatchCycleAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in result.Items.Where(x => x.State == TransportProductionQueueState.Assigned))
        {
            _telemetry.RecordQueueWait(
                Math.Max(0, (item.UpdatedAtUtc - item.ProductionRequest.EnqueuedAtUtc).TotalMilliseconds),
                item.ProductionRequest.DestinationStationId);
        }
        return result;
    }

    public IReadOnlyList<TransportProductionQueueItem> GetQueue() => _inner.GetQueue();

    public TransportProductionDryRunReport DryRun(DateTime? nowUtc = null) => _inner.DryRun(nowUtc);

    public IReadOnlyList<TransportDispatchDecisionFrame> GetDecisions(int maxCount = 500) =>
        _inner.GetDecisions(maxCount);
}
