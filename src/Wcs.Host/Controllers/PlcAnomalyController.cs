namespace Wcs.Host.Controllers;

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

[ApiController]
[Route("api/anomaly")]
public sealed class PlcAnomalyController : ControllerBase
{
    private readonly IPlcAnomalyEngine _engine;
    private readonly IPlcAnomalyStatusProvider _statusProvider;
    private readonly IEventBus _eventBus;
    private readonly IHostEnvironment _environment;

    public PlcAnomalyController(
        IPlcAnomalyEngine engine,
        IPlcAnomalyStatusProvider statusProvider,
        IEventBus eventBus,
        IHostEnvironment environment)
    {
        _engine = engine;
        _statusProvider = statusProvider;
        _eventBus = eventBus;
        _environment = environment;
    }

    [HttpGet("status")]
    public ActionResult<PlcAnomalyStatus> GetStatus() => Ok(_statusProvider.GetStatus());

    [HttpGet("active")]
    public ActionResult<IReadOnlyList<PlcAnomalyRecord>> GetActive() => Ok(_engine.GetActiveAnomalies());

    /// <summary>
    /// 仅 LoadTest 环境启用。生成阈值异常、跨信号一致性异常和大量正常值，
    /// 同时校验生命周期守恒与零误报。
    /// </summary>
    [HttpPost("load")]
    public async Task<ActionResult> GenerateLoad(
        [FromBody] PlcAnomalyLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();

        var cycles = Math.Clamp(request.Cycles, 1, 20_000);
        var consistencyCycles = Math.Clamp(request.ConsistencyCycles, 0, 20_000);
        var concurrency = Math.Clamp(request.Concurrency, 1, 64);
        var normalEvents = Math.Clamp(request.NormalEvents, 0, 1_000_000);
        var before = _statusProvider.GetStatus();
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, cycles),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                var device = $"ANOMCV{index:D6}";
                var fieldName = $"{device}_ANOMALY_LOAD_Current";
                for (var sample = 0; sample < 3; sample++)
                    await PublishNumericAsync(fieldName, 20 + sample, ct);
                for (var sample = 0; sample < 2; sample++)
                    await PublishNumericAsync(fieldName, 5, ct);
            });

        await Parallel.ForEachAsync(
            Enumerable.Range(0, consistencyCycles),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                var device = $"ANOMCONS{index:D6}";
                await PublishNumericAsync($"{device}_ANOMALY_LOAD_Speed", 0, ct);
                await PublishBooleanAsync($"{device}_ANOMALY_LOAD_Running", true, ct);
                await PublishNumericAsync($"{device}_ANOMALY_LOAD_Speed", 2, ct);
            });

        await Parallel.ForEachAsync(
            Enumerable.Range(0, normalEvents),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                var device = $"ANOMNORMAL{index % 100:D3}";
                await PublishNumericAsync($"{device}_ANOMALY_LOAD_Current", 5 + index % 3, ct);
            });

        stopwatch.Stop();
        var after = _statusProvider.GetStatus();
        var totalEvents = cycles * 5L + consistencyCycles * 3L + normalEvents;
        return Ok(new
        {
            cycles,
            consistencyCycles,
            concurrency,
            normalEvents,
            totalEvents,
            elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            eventsPerSecond = totalEvents / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001),
            processedDelta = after.ProcessedSamples - before.ProcessedSamples,
            raisedDelta = after.Raised - before.Raised,
            recoveredDelta = after.Recovered - before.Recovered,
            failureDelta = after.Failures - before.Failures,
            suppressedDelta = after.Suppressed - before.Suppressed,
            activeDelta = after.ActiveAnomalies - before.ActiveAnomalies,
            status = after
        });
    }

    private Task PublishNumericAsync(
        string fieldName,
        double value,
        CancellationToken cancellationToken) =>
        PublishAsync(fieldName, value.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    private Task PublishBooleanAsync(
        string fieldName,
        bool value,
        CancellationToken cancellationToken) =>
        PublishAsync(fieldName, value.ToString(), cancellationToken);

    private Task PublishAsync(
        string fieldName,
        string value,
        CancellationToken cancellationToken) =>
        _eventBus.PublishAsync(new RawSignalEvent
        {
            PlcName = "ANOMALY-LOAD-PLC",
            DbBlock = 900,
            FieldName = fieldName,
            OldValue = null,
            NewValue = value,
            Edge = "Changed",
            ValidatorPassed = true,
            ValidatorReason = "anomaly-load-test",
            DomainEventType = "AnomalyLoad"
        }, cancellationToken);
}

public sealed class PlcAnomalyLoadRequest
{
    public int Cycles { get; set; } = 2_000;
    public int ConsistencyCycles { get; set; } = 1_000;
    public int Concurrency { get; set; } = 32;
    public int NormalEvents { get; set; } = 100_000;
}
