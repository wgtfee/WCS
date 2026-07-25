namespace Wcs.Host.Controllers;

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;

[ApiController]
[Route("api/anomaly/ml/load")]
public sealed class PlcMlLoadController : ControllerBase
{
    private static readonly DateTime ProcessAnchorUtc = AlignToSecond(DateTime.UtcNow.AddHours(1));
    private readonly IPlcMlAnomalyEngine _engine;
    private readonly IHostEnvironment _environment;

    public PlcMlLoadController(IPlcMlAnomalyEngine engine, IHostEnvironment environment)
    {
        _engine = engine;
        _environment = environment;
    }

    [HttpPost]
    public async Task<ActionResult> Generate(
        [FromBody] PlcMlLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("MlLoadTest")) return NotFound();

        var mode = request.Mode.Trim().ToLowerInvariant();
        if (mode is not ("normal" or "training" or "anomaly" or "recovery"))
            return BadRequest(new { error = "Mode 必须是 normal、training、anomaly 或 recovery。" });

        var devices = Math.Clamp(request.Devices, 1, 2_000);
        var windows = Math.Clamp(request.Windows, 1, 2_000);
        var concurrency = Math.Clamp(request.Concurrency, 1, 64);
        var deviceOffset = Math.Clamp(request.DeviceOffset, 0, 1_000_000);
        var startOffsetSeconds = Math.Clamp(request.StartOffsetSeconds, 0, 10_000_000);
        var before = GetProfileStatus();
        var stopwatch = Stopwatch.StartNew();

        await GenerateWindowsAsync(
            mode,
            devices,
            windows,
            deviceOffset,
            startOffsetSeconds,
            concurrency,
            cancellationToken);
        await _engine.MaintenanceAsync(
            ProcessAnchorUtc.AddSeconds(startOffsetSeconds + windows + 1),
            cancellationToken);

        var afterEvaluation = GetProfileStatus();
        var cleanupRecovered = 0L;
        var finalStatus = afterEvaluation;
        if (mode == "normal" && afterEvaluation.ActiveAnomalies > 0)
        {
            await GenerateWindowsAsync(
                "recovery",
                devices,
                2,
                deviceOffset,
                startOffsetSeconds + windows,
                concurrency,
                cancellationToken);
            await _engine.MaintenanceAsync(
                ProcessAnchorUtc.AddSeconds(startOffsetSeconds + windows + 3),
                cancellationToken);
            finalStatus = GetProfileStatus();
            cleanupRecovered = finalStatus.Recovered - afterEvaluation.Recovered;
        }

        stopwatch.Stop();
        return Ok(new
        {
            mode,
            devices,
            windows,
            totalSamples = devices * windows * 3L,
            elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            samplesPerSecond = devices * windows * 3L / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001),
            completedWindowDelta = afterEvaluation.CompletedWindows - before.CompletedWindows,
            droppedWindowDelta = afterEvaluation.DroppedIncompleteWindows - before.DroppedIncompleteWindows,
            trainingWindowDelta = afterEvaluation.TrainingWindowCount - before.TrainingWindowCount,
            predictionDelta = afterEvaluation.Predictions - before.Predictions,
            anomalyObservationDelta = afterEvaluation.AnomalyObservations - before.AnomalyObservations,
            raisedDelta = afterEvaluation.Raised - before.Raised,
            recoveredDelta = afterEvaluation.Recovered - before.Recovered,
            cleanupRecoveredDelta = cleanupRecovered,
            failureDelta = finalStatus.Failures - before.Failures,
            status = finalStatus
        });
    }

    private async Task GenerateWindowsAsync(
        string mode,
        int devices,
        int windows,
        int deviceOffset,
        int startOffsetSeconds,
        int concurrency,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            Enumerable.Range(0, devices),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (deviceIndex, ct) =>
            {
                var deviceNumber = deviceOffset + deviceIndex;
                var deviceId = $"MLCV{deviceNumber:D6}";
                for (var windowIndex = 0; windowIndex < windows; windowIndex++)
                {
                    var windowStart = ProcessAnchorUtc.AddSeconds(startOffsetSeconds + windowIndex);
                    for (var sampleIndex = 0; sampleIndex < 3; sampleIndex++)
                    {
                        var value = ResolveValue(mode, deviceNumber, windowIndex, sampleIndex);
                        await _engine.ProcessAsync(new PlcAnomalySample
                        {
                            EventId = Guid.NewGuid().ToString("N"),
                            TimestampUtc = windowStart.AddMilliseconds(100 + sampleIndex * 300),
                            PlcName = "ML-PLC",
                            DbBlock = 950,
                            DeviceId = deviceId,
                            SignalName = $"{deviceId}_Current",
                            NewValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            NumericValue = value,
                            Source = "MlLoadTest"
                        }, ct);
                    }
                }
            });
    }

    private PlcMlProfileStatus GetProfileStatus() =>
        _engine.GetStatus().Single(status => status.ProfileId == "ML-CV-CURRENT");

    private static DateTime AlignToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static double ResolveValue(string mode, int device, int window, int sample)
    {
        if (mode == "anomaly")
            return 19.5 + device % 5 * 0.2 + window * 0.15 + sample * 0.1;

        // Recovery repeats a real central training window (device 0 / window 0).
        var phase = mode == "recovery"
            ? sample * 0.19
            : device * 0.071 + window * 0.113 + sample * 0.19;
        return 5.0 + Math.Sin(phase) * 0.22 + Math.Cos(phase * 0.37) * 0.05;
    }
}

public sealed class PlcMlLoadRequest
{
    public string Mode { get; set; } = "normal";
    public int Devices { get; set; } = 100;
    public int Windows { get; set; } = 5;
    public int DeviceOffset { get; set; }
    public int StartOffsetSeconds { get; set; }
    public int Concurrency { get; set; } = 16;
}
