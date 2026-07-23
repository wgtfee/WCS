namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.Telemetry;

[ApiController]
[Route("api/telemetry")]
public sealed class PlcTelemetryController : ControllerBase
{
    private readonly IPlcTelemetryStatusProvider _statusProvider;
    private readonly IEventBus _eventBus;
    private readonly IHostEnvironment _environment;

    public PlcTelemetryController(
        IPlcTelemetryStatusProvider statusProvider,
        IEventBus eventBus,
        IHostEnvironment environment)
    {
        _statusProvider = statusProvider;
        _eventBus = eventBus;
        _environment = environment;
    }

    [HttpGet("status")]
    public ActionResult<PlcTelemetryStatus> GetStatus() => Ok(_statusProvider.GetStatus());

    /// <summary>
    /// 仅 LoadTest 环境可用。通过真实 EventBus 链路生成 RawSignalEvent，
    /// 用于校验生产数量、队列数量和最终持久化数量是否守恒。
    /// </summary>
    [HttpPost("load")]
    public async Task<ActionResult> GenerateLoad(
        [FromBody] PlcTelemetryLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();

        var count = Math.Clamp(request.Count, 1, 1_000_000);
        var concurrency = Math.Clamp(request.Concurrency, 1, 64);
        var before = _statusProvider.GetStatus();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                var mode = index % 3;
                var value = mode switch
                {
                    0 => (index & 1) == 0 ? "true" : "false",
                    1 => (index * 0.125).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => $"STATE-{index % 17}"
                };

                await _eventBus.PublishAsync(new RawSignalEvent
                {
                    PlcName = $"LOAD-PLC-{(index % 4) + 1}",
                    DbBlock = (index % 6) + 1,
                    FieldName = $"LOAD-CV-{(index % 32) + 1}_Signal_{index % 24}",
                    OldValue = mode == 0 ? "false" : null,
                    NewValue = value,
                    Edge = mode == 0 ? "Rising" : "Changed",
                    ValidatorPassed = true,
                    ValidatorReason = "telemetry-load-test",
                    DomainEventType = "TelemetryLoad"
                }, ct);
            });

        var after = _statusProvider.GetStatus();
        return Ok(new
        {
            generated = count,
            concurrency,
            acceptedDelta = after.Accepted - before.Accepted,
            droppedDelta = after.Dropped - before.Dropped,
            status = after
        });
    }
}

public sealed class PlcTelemetryLoadRequest
{
    public int Count { get; set; } = 100_000;
    public int Concurrency { get; set; } = 16;
}
