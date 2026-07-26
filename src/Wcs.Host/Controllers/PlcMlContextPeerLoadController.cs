namespace Wcs.Host.Controllers;

using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// MlLoadTest 环境专用的同群设备端到端负载入口。
/// 所有样本均发布为 RawSignalEvent，完整经过 EventBus、SampleFactory、运行上下文、
/// 独立特征窗口、Peer Median/MAD、治理候选和正式异常生命周期。
/// </summary>
[ApiController]
[Route("api/anomaly/ml/context-peer/load")]
public sealed class PlcMlContextPeerLoadController : ControllerBase
{
    private static readonly DateTime ProcessAnchorUtc = AlignToSecond(DateTime.UtcNow.AddHours(2));
    private readonly IEventBus _eventBus;
    private readonly IPlcMlContextPeerRuntime _runtime;
    private readonly IHostEnvironment _environment;

    public PlcMlContextPeerLoadController(
        IEventBus eventBus,
        IPlcMlContextPeerRuntime runtime,
        IHostEnvironment environment)
    {
        _eventBus = eventBus;
        _runtime = runtime;
        _environment = environment;
    }

    [HttpPost]
    public async Task<ActionResult> Generate(
        [FromBody] PlcMlContextPeerLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsEnvironment("MlLoadTest")) return NotFound();

        var mode = request.Mode.Trim().ToLowerInvariant();
        if (mode is not ("normal" or "anomaly" or "recovery"))
            return BadRequest(new { error = "Mode 必须是 normal、anomaly 或 recovery。" });

        var groups = Math.Clamp(request.Groups, 1, 500);
        var devicesPerGroup = Math.Clamp(request.DevicesPerGroup, 3, 100);
        var windows = Math.Clamp(request.Windows, 1, 100);
        var outlierIndex = Math.Clamp(request.OutlierIndex, 0, devicesPerGroup - 1);
        var concurrency = Math.Clamp(request.Concurrency, 1, 64);
        var deviceOffset = Math.Clamp(request.DeviceOffset, 0, 900_000);
        var startOffsetSeconds = Math.Clamp(request.StartOffsetSeconds, 0, 10_000_000);
        var contextPrefix = string.IsNullOrWhiteSpace(request.ContextPrefix)
            ? "PEER"
            : request.ContextPrefix.Trim();

        if (deviceOffset + groups * devicesPerGroup > 999_999)
            return BadRequest(new { error = "设备编号范围超过六位负载测试编号上限。" });

        var before = GetProfileStatus();
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, groups),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (groupIndex, ct) =>
            {
                await GenerateGroupAsync(
                    mode,
                    groupIndex,
                    devicesPerGroup,
                    windows,
                    outlierIndex,
                    deviceOffset,
                    startOffsetSeconds,
                    contextPrefix,
                    ct);
            });

        await _runtime.MaintenanceAsync(
            ProcessAnchorUtc.AddSeconds(startOffsetSeconds + windows + 2),
            cancellationToken);

        stopwatch.Stop();
        var after = GetProfileStatus();
        var peerBefore = before.Peer;
        var peerAfter = after.Peer;
        var totalSamples = groups * devicesPerGroup * (1L + windows * 3L);

        return Ok(new
        {
            pipeline = "RawSignalEvent->EventBus->SampleFactory->OperatingContext->PeerWindow->MedianMAD->Governance/SQL",
            mode,
            groups,
            devicesPerGroup,
            windows,
            outlierIndex,
            deviceOffset,
            contextPrefix,
            totalSamples,
            elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            samplesPerSecond = totalSamples / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001),
            completedWindowDelta = after.CompletedWindows - before.CompletedWindows,
            droppedWindowDelta = after.DroppedIncompleteWindows - before.DroppedIncompleteWindows,
            bucketDelta = peerAfter.BucketsEvaluated - peerBefore.BucketsEvaluated,
            deviceEvaluationDelta = peerAfter.DevicesEvaluated - peerBefore.DevicesEvaluated,
            raisedDelta = peerAfter.Raised - peerBefore.Raised,
            recoveredDelta = peerAfter.Recovered - peerBefore.Recovered,
            shadowRaisedDelta = peerAfter.ShadowRaised - peerBefore.ShadowRaised,
            activeRaisedDelta = peerAfter.ActiveRaised - peerBefore.ActiveRaised,
            skippedBucketDelta = peerAfter.SkippedBuckets - peerBefore.SkippedBuckets,
            failureDelta = peerAfter.Failures - peerBefore.Failures,
            status = after
        });
    }

    private async Task GenerateGroupAsync(
        string mode,
        int groupIndex,
        int devicesPerGroup,
        int windows,
        int outlierIndex,
        int deviceOffset,
        int startOffsetSeconds,
        string contextPrefix,
        CancellationToken cancellationToken)
    {
        var groupDeviceOffset = deviceOffset + groupIndex * devicesPerGroup;
        var context = $"{contextPrefix}-G{groupIndex:D4}";
        var firstWindow = ProcessAnchorUtc.AddSeconds(startOffsetSeconds);

        for (var deviceIndex = 0; deviceIndex < devicesPerGroup; deviceIndex++)
        {
            var deviceNumber = groupDeviceOffset + deviceIndex;
            var deviceId = $"MLCV{deviceNumber:D6}";
            await _eventBus.PublishAsync(new RawSignalEvent
            {
                SourceTimestampUtc = firstWindow.AddMilliseconds(-100),
                PlcName = "ML-PLC",
                DbBlock = 951,
                FieldName = $"{deviceId}_Mode",
                NewValue = context,
                ValidatorPassed = true,
                DomainEventType = "MlContextPeerLoadTest"
            }, cancellationToken);
        }

        for (var windowIndex = 0; windowIndex < windows; windowIndex++)
        {
            var windowStart = firstWindow.AddSeconds(windowIndex);
            for (var deviceIndex = 0; deviceIndex < devicesPerGroup; deviceIndex++)
            {
                var deviceNumber = groupDeviceOffset + deviceIndex;
                var deviceId = $"MLCV{deviceNumber:D6}";
                for (var sampleIndex = 0; sampleIndex < 3; sampleIndex++)
                {
                    var value = ResolveValue(mode, groupIndex, deviceIndex, outlierIndex, sampleIndex);
                    await _eventBus.PublishAsync(new RawSignalEvent
                    {
                        SourceTimestampUtc = windowStart.AddMilliseconds(100 + sampleIndex * 300),
                        PlcName = "ML-PLC",
                        DbBlock = 951,
                        FieldName = $"{deviceId}_Current",
                        NewValue = value.ToString(CultureInfo.InvariantCulture),
                        ValidatorPassed = true,
                        DomainEventType = "MlContextPeerLoadTest"
                    }, cancellationToken);
                }
            }
        }
    }

    private PlcMlContextPeerProfileStatus GetProfileStatus() =>
        _runtime.GetStatus().Single(status => status.ProfileId == "ML-CV-CURRENT");

    private static double ResolveValue(
        string mode,
        int groupIndex,
        int deviceIndex,
        int outlierIndex,
        int sampleIndex)
    {
        if (mode == "anomaly" && deviceIndex == outlierIndex)
            return 20.0 + groupIndex * 0.001 + sampleIndex * 0.01;

        // 正常设备保持极小确定性差异，既模拟真实噪声，也确保中位数/MAD 结果可重复。
        return 5.0 + deviceIndex * 0.001 + sampleIndex * 0.0005;
    }

    private static DateTime AlignToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
}

public sealed class PlcMlContextPeerLoadRequest
{
    public string Mode { get; set; } = "normal";
    public int Groups { get; set; } = 20;
    public int DevicesPerGroup { get; set; } = 10;
    public int Windows { get; set; } = 1;
    public int OutlierIndex { get; set; } = 9;
    public int DeviceOffset { get; set; } = 500_000;
    public int StartOffsetSeconds { get; set; }
    public int Concurrency { get; set; } = 8;
    public string ContextPrefix { get; set; } = "PEER";
}
