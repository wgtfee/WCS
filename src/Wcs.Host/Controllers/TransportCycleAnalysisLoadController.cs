namespace Wcs.Host.Controllers;

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

/// <summary>
/// LoadTest 环境专用的周期模型确定性负载入口。
/// 直接向只读周期分析服务送入执行快照，不修改调度、车辆或 PLC 状态。
/// </summary>
[ApiController]
[Route("api/transport/cycle-analysis/load")]
public sealed class TransportCycleAnalysisLoadController : ControllerBase
{
    private static readonly DateTime AnchorUtc = new(2036, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly ITransportCycleAnalysisService _analysis;
    private readonly IHostEnvironment _environment;

    public TransportCycleAnalysisLoadController(
        ITransportCycleAnalysisService analysis,
        IHostEnvironment environment)
    {
        _analysis = analysis;
        _environment = environment;
    }

    [HttpPost("scenario")]
    public ActionResult RunScenario([FromBody] TransportCycleLoadRequest request)
    {
        if (!_environment.IsEnvironment("LoadTest")) return NotFound();

        var baselineCycles = Math.Clamp(request.BaselineCycles, 20, 20_000);
        var slowCycles = Math.Clamp(request.SlowCycles, 1, 5_000);
        var invalidCycles = Math.Clamp(request.InvalidCycles, 1, 5_000);
        var faultedCycles = Math.Clamp(request.FaultedCycles, 1, 5_000);
        var alternateContextCycles = Math.Clamp(request.AlternateContextCycles, 1, 10_000);
        var prefix = string.IsNullOrWhiteSpace(request.RequestPrefix)
            ? "CYCLE-E2E"
            : request.RequestPrefix.Trim();

        var before = _analysis.GetStatus();
        var beforeCycleCount = _analysis.GetCycles(int.MaxValue).Count;
        var beforeAnomalyCount = _analysis.GetAnomalies(int.MaxValue).Count;
        var stopwatch = Stopwatch.StartNew();
        var cursor = 0;

        for (var index = 0; index < baselineCycles; index++, cursor++)
        {
            var movementMs = 300 + ((index % 5) - 2) * 2;
            CompleteValidCycle(
                $"{prefix}-BASE-{index:D6}",
                AnchorUtc.AddSeconds(cursor * 10L),
                movementMs,
                destination: "DST-A");
        }

        for (var index = 0; index < slowCycles; index++, cursor++)
        {
            CompleteValidCycle(
                $"{prefix}-SLOW-{index:D6}",
                AnchorUtc.AddSeconds(cursor * 10L),
                movingToDestinationMs: 1_000,
                destination: "DST-A");
        }

        for (var index = 0; index < invalidCycles; index++, cursor++)
        {
            var requestId = $"{prefix}-INVALID-{index:D6}";
            var start = AnchorUtc.AddSeconds(cursor * 10L);
            var assigned = Snapshot(requestId, TransportExecutionState.Assigned, start, start, "DST-A");
            _analysis.Observe(null, assigned, "Create", true);
            _analysis.Observe(
                assigned,
                Snapshot(requestId, TransportExecutionState.Completed, start, start.AddMilliseconds(900), "DST-A"),
                "InvalidComplete",
                true);
        }

        for (var index = 0; index < faultedCycles; index++, cursor++)
        {
            CompleteFaultedCycle(
                $"{prefix}-FAULT-{index:D6}",
                AnchorUtc.AddSeconds(cursor * 10L),
                destination: "DST-A");
        }

        // 另一条路线没有足够历史基线，即使耗时较慢，也不应借用 DST-A 的基线。
        for (var index = 0; index < alternateContextCycles; index++, cursor++)
        {
            CompleteValidCycle(
                $"{prefix}-ALT-{index:D6}",
                AnchorUtc.AddSeconds(cursor * 10L),
                movingToDestinationMs: 1_500,
                destination: "DST-B");
        }

        stopwatch.Stop();
        var after = _analysis.GetStatus();
        var cycles = _analysis.GetCycles(int.MaxValue)
            .Where(cycle => cycle.RequestId.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        var anomalies = _analysis.GetAnomalies(int.MaxValue)
            .Where(anomaly => anomaly.RequestId.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();

        return Ok(new
        {
            pipeline = "ExecutionSnapshot->CycleTracker->PhaseDurations->RouteLoadContext->MedianMAD->ReadOnlyAPI",
            prefix,
            baselineCycles,
            slowCycles,
            invalidCycles,
            faultedCycles,
            alternateContextCycles,
            elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            cyclesPerSecond = cycles.Length / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001),
            cycleDelta = after.CompletedCycles - before.CompletedCycles,
            successfulDelta = after.SuccessfulCycles - before.SuccessfulCycles,
            interruptedDelta = after.InterruptedCycles - before.InterruptedCycles,
            invalidSequenceDelta = after.InvalidSequenceAnomalies - before.InvalidSequenceAnomalies,
            durationAnomalyDelta = after.DurationAnomalies - before.DurationAnomalies,
            observedTransitionDelta = after.ObservedTransitions - before.ObservedTransitions,
            baselineContextDelta = after.BaselineContexts - before.BaselineContexts,
            retainedCycleDelta = _analysis.GetCycles(int.MaxValue).Count - beforeCycleCount,
            retainedAnomalyDelta = _analysis.GetAnomalies(int.MaxValue).Count - beforeAnomalyCount,
            slowTotalAnomalies = anomalies.Count(anomaly =>
                anomaly.Kind == TransportCycleAnomalyKind.TotalDuration &&
                anomaly.RequestId.Contains("-SLOW-", StringComparison.Ordinal)),
            slowPhaseAnomalies = anomalies.Count(anomaly =>
                anomaly.Kind == TransportCycleAnomalyKind.PhaseDuration &&
                anomaly.RequestId.Contains("-SLOW-", StringComparison.Ordinal)),
            alternateContextAnomalies = anomalies.Count(anomaly =>
                anomaly.RequestId.Contains("-ALT-", StringComparison.Ordinal)),
            invalidSequenceAnomalies = anomalies.Count(anomaly =>
                anomaly.Kind == TransportCycleAnomalyKind.InvalidSequence),
            validSuccessfulCycles = cycles.Count(cycle => cycle.IsSuccessful),
            invalidCompletedCycles = cycles.Count(cycle =>
                cycle.TerminalState == TransportExecutionState.Completed && !cycle.IsSequenceValid),
            faultedAuditCycles = cycles.Count(cycle => cycle.TerminalState == TransportExecutionState.Faulted),
            status = after
        });
    }

    private void CompleteValidCycle(
        string requestId,
        DateTime start,
        double movingToDestinationMs,
        string destination)
    {
        var assigned = Snapshot(requestId, TransportExecutionState.Assigned, start, start, destination);
        _analysis.Observe(null, assigned, "Create", true);

        var movingToPickup = Snapshot(
            requestId,
            TransportExecutionState.MovingToPickup,
            start,
            start.AddMilliseconds(100),
            destination);
        _analysis.Observe(assigned, movingToPickup, "Start", true);

        var loading = Snapshot(
            requestId,
            TransportExecutionState.Loading,
            start,
            start.AddMilliseconds(400),
            destination);
        _analysis.Observe(movingToPickup, loading, "PositionFeedback", true);

        var movingToDestination = Snapshot(
            requestId,
            TransportExecutionState.MovingToDestination,
            start,
            start.AddMilliseconds(500),
            destination);
        _analysis.Observe(loading, movingToDestination, "ConfirmLoaded", true);

        var unloadingAt = 500 + movingToDestinationMs;
        var unloading = Snapshot(
            requestId,
            TransportExecutionState.Unloading,
            start,
            start.AddMilliseconds(unloadingAt),
            destination);
        _analysis.Observe(movingToDestination, unloading, "PositionFeedback", true);

        var completed = Snapshot(
            requestId,
            TransportExecutionState.Completed,
            start,
            start.AddMilliseconds(unloadingAt + 200),
            destination);
        _analysis.Observe(unloading, completed, "ConfirmUnloaded", true);
    }

    private void CompleteFaultedCycle(string requestId, DateTime start, string destination)
    {
        var assigned = Snapshot(requestId, TransportExecutionState.Assigned, start, start, destination);
        _analysis.Observe(null, assigned, "Create", true);
        var movingToPickup = Snapshot(
            requestId,
            TransportExecutionState.MovingToPickup,
            start,
            start.AddMilliseconds(100),
            destination);
        _analysis.Observe(assigned, movingToPickup, "Start", true);
        var loading = Snapshot(
            requestId,
            TransportExecutionState.Loading,
            start,
            start.AddMilliseconds(400),
            destination);
        _analysis.Observe(movingToPickup, loading, "PositionFeedback", true);
        var movingToDestination = Snapshot(
            requestId,
            TransportExecutionState.MovingToDestination,
            start,
            start.AddMilliseconds(500),
            destination);
        _analysis.Observe(loading, movingToDestination, "ConfirmLoaded", true);
        var faulted = Snapshot(
            requestId,
            TransportExecutionState.Faulted,
            start,
            start.AddMilliseconds(900),
            destination,
            "simulated cycle fault");
        _analysis.Observe(movingToDestination, faulted, "Fault", true);

        // 后续取消属于终态后的重复操作，不得形成第二条周期。
        _analysis.Observe(
            faulted,
            faulted with { LastError = "post-fault cancel ignored", UpdatedAtUtc = start.AddMilliseconds(950) },
            "Cancel",
            false);
    }

    private static TransportExecutionSnapshot Snapshot(
        string requestId,
        TransportExecutionState state,
        DateTime createdAt,
        DateTime updatedAt,
        string destination,
        string? lastError = null)
    {
        var afterPickup = state is
            TransportExecutionState.Loading or
            TransportExecutionState.MovingToDestination or
            TransportExecutionState.Unloading or
            TransportExecutionState.Completed or
            TransportExecutionState.Faulted;
        return new TransportExecutionSnapshot
        {
            RequestId = requestId,
            AssignmentId = $"ASSIGN-{requestId}",
            VehicleId = $"RGV-{requestId}",
            LoadId = $"LOAD-{requestId}",
            State = state,
            CurrentNodeId = afterPickup ? "PICK" : "SRC",
            CurrentNodeIndex = afterPickup ? 1 : 0,
            PickupNodeIndex = 1,
            FullNodePath = new[] { "SRC", "PICK", destination },
            FullEdgePath = new[] { "E1", "E2" },
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt,
            LastError = lastError
        };
    }
}

public sealed class TransportCycleLoadRequest
{
    public string RequestPrefix { get; set; } = "CYCLE-E2E";
    public int BaselineCycles { get; set; } = 100;
    public int SlowCycles { get; set; } = 20;
    public int InvalidCycles { get; set; } = 10;
    public int FaultedCycles { get; set; } = 10;
    public int AlternateContextCycles { get; set; } = 10;
}
