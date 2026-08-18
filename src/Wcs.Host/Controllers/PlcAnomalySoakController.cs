namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

[ApiController]
[Route("api/anomaly/soak")]
public sealed class PlcAnomalySoakController : ControllerBase
{
    private readonly IPlcAnomalyStatusProvider _statusProvider;
    private readonly IEventBus _eventBus;
    private readonly IHostEnvironment _environment;

    public PlcAnomalySoakController(
        IPlcAnomalyStatusProvider statusProvider,
        IEventBus eventBus,
        IHostEnvironment environment)
    {
        _statusProvider = statusProvider;
        _eventBus = eventBus;
        _environment = environment;
    }

    [HttpPost]
    public async Task<ActionResult> Generate(
        [FromBody] PlcAnomalySoakRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();

        var thresholdCycles = Math.Clamp(request.ThresholdCycles, 1, 2_000);
        var consistencyCycles = Math.Clamp(request.ConsistencyCycles, 0, 2_000);
        var normalEvents = Math.Clamp(request.NormalEvents, 0, 100_000);
        var concurrency = Math.Clamp(request.Concurrency, 1, 64);
        var offset = Math.Clamp(request.DeviceOffset, 0, 1_000_000_000);
        var before = _statusProvider.GetStatus();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, thresholdCycles),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                var signal = $"ANOMCV{offset + index:D9}_ANOMALY_LOAD_Current";
                await PublishAsync(signal, "20", ct);
                await PublishAsync(signal, "21", ct);
                await PublishAsync(signal, "22", ct);
                await PublishAsync(signal, "5", ct);
                await PublishAsync(signal, "5", ct);
            });

        await Parallel.ForEachAsync(
            Enumerable.Range(0, consistencyCycles),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                var device = $"ANOMCONS{offset + index:D9}";
                await PublishAsync($"{device}_ANOMALY_LOAD_Speed", "0", ct);
                await PublishAsync($"{device}_ANOMALY_LOAD_Running", "true", ct);
                await PublishAsync($"{device}_ANOMALY_LOAD_Speed", "2", ct);
            });

        await Parallel.ForEachAsync(
            Enumerable.Range(0, normalEvents),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                var signal = $"ANOMNORMAL{index % 100:D3}_ANOMALY_LOAD_Current";
                await PublishAsync(signal, (5 + index % 3).ToString(), ct);
            });

        var after = _statusProvider.GetStatus();
        return Ok(new
        {
            thresholdCycles,
            consistencyCycles,
            normalEvents,
            deviceOffset = offset,
            totalEvents = thresholdCycles * 5L + consistencyCycles * 3L + normalEvents,
            processedDelta = after.ProcessedSamples - before.ProcessedSamples,
            raisedDelta = after.Raised - before.Raised,
            recoveredDelta = after.Recovered - before.Recovered,
            failureDelta = after.Failures - before.Failures,
            suppressedDelta = after.Suppressed - before.Suppressed,
            activeDelta = after.ActiveAnomalies - before.ActiveAnomalies
        });
    }

    private Task PublishAsync(string fieldName, string value, CancellationToken cancellationToken) =>
        _eventBus.PublishAsync(new RawSignalEvent
        {
            PlcName = "ANOMALY-LOAD-PLC",
            DbBlock = 901,
            FieldName = fieldName,
            NewValue = value,
            Edge = "Changed",
            ValidatorPassed = true,
            ValidatorReason = "anomaly-soak-test",
            DomainEventType = "AnomalySoak"
        }, cancellationToken);
}

public sealed class PlcAnomalySoakRequest
{
    public int ThresholdCycles { get; set; } = 100;
    public int ConsistencyCycles { get; set; } = 50;
    public int NormalEvents { get; set; } = 5_000;
    public int DeviceOffset { get; set; }
    public int Concurrency { get; set; } = 32;
}
