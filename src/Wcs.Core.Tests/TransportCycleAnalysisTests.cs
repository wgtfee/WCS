namespace Wcs.Core.Tests;

using Wcs.Core.TransportScheduling;

public sealed class TransportCycleAnalysisTests
{
    [Fact]
    public void Normal_cycle_records_each_phase_and_same_state_feedback_does_not_split_phase()
    {
        var service = CreateService(minimumBaselineCycles: 3);
        var start = Utc(0);
        var requestId = "CYCLE-001";

        service.Observe(null, Snapshot(requestId, TransportExecutionState.Assigned, start, start), "Create", true);
        service.Observe(
            Snapshot(requestId, TransportExecutionState.Assigned, start, start),
            Snapshot(requestId, TransportExecutionState.MovingToPickup, start, start.AddMilliseconds(100)),
            "Start",
            true);
        service.Observe(
            Snapshot(requestId, TransportExecutionState.MovingToPickup, start, start.AddMilliseconds(100)),
            Snapshot(requestId, TransportExecutionState.MovingToPickup, start, start.AddMilliseconds(250), feedback: 1),
            "ApplyPositionFeedback",
            true);
        service.Observe(
            Snapshot(requestId, TransportExecutionState.MovingToPickup, start, start.AddMilliseconds(250), feedback: 1),
            Snapshot(requestId, TransportExecutionState.Loading, start, start.AddMilliseconds(400), feedback: 2),
            "ApplyPositionFeedback",
            true);
        service.Observe(
            Snapshot(requestId, TransportExecutionState.Loading, start, start.AddMilliseconds(400)),
            Snapshot(requestId, TransportExecutionState.MovingToDestination, start, start.AddMilliseconds(500)),
            "ConfirmLoaded",
            true);
        service.Observe(
            Snapshot(requestId, TransportExecutionState.MovingToDestination, start, start.AddMilliseconds(500)),
            Snapshot(requestId, TransportExecutionState.Unloading, start, start.AddMilliseconds(800)),
            "ApplyPositionFeedback",
            true);
        service.Observe(
            Snapshot(requestId, TransportExecutionState.Unloading, start, start.AddMilliseconds(800)),
            Snapshot(requestId, TransportExecutionState.Completed, start, start.AddMilliseconds(1000)),
            "ConfirmUnloaded",
            true);

        var cycle = Assert.Single(service.GetCycles());
        Assert.True(cycle.IsSuccessful);
        Assert.True(cycle.IsSequenceValid);
        Assert.Equal(1000, cycle.TotalDurationMilliseconds);
        Assert.Collection(
            cycle.Phases,
            phase => AssertPhase(phase, TransportExecutionState.Assigned, 100),
            phase => AssertPhase(phase, TransportExecutionState.MovingToPickup, 300),
            phase => AssertPhase(phase, TransportExecutionState.Loading, 100),
            phase => AssertPhase(phase, TransportExecutionState.MovingToDestination, 300),
            phase => AssertPhase(phase, TransportExecutionState.Unloading, 200));
        Assert.Empty(service.GetAnomalies());
        Assert.Equal(1, service.GetStatus().SuccessfulCycles);
    }

    [Fact]
    public void Invalid_completed_and_faulted_cycles_do_not_enter_normal_baseline()
    {
        var service = CreateService(minimumBaselineCycles: 3);
        var start = Utc(0);

        service.Observe(null, Snapshot("INVALID", TransportExecutionState.Assigned, start, start), "Create", true);
        service.Observe(
            Snapshot("INVALID", TransportExecutionState.Assigned, start, start),
            Snapshot("INVALID", TransportExecutionState.Completed, start, start.AddSeconds(1)),
            "InvalidComplete",
            true);

        CompleteCycle(service, "FAULT", start.AddSeconds(10), movingToDestinationMs: 300, terminal: TransportExecutionState.Faulted);

        var cycles = service.GetCycles(10).OrderBy(cycle => cycle.RequestId).ToArray();
        Assert.Equal(2, cycles.Length);
        var invalid = Assert.Single(cycles.Where(cycle => cycle.RequestId == "INVALID"));
        Assert.False(invalid.IsSequenceValid);
        Assert.False(invalid.IsSuccessful);
        var faulted = Assert.Single(cycles.Where(cycle => cycle.RequestId == "FAULT"));
        Assert.Equal(TransportExecutionState.Faulted, faulted.TerminalState);
        Assert.False(faulted.IsSuccessful);

        var anomaly = Assert.Single(service.GetAnomalies());
        Assert.Equal(TransportCycleAnomalyKind.InvalidSequence, anomaly.Kind);
        var status = service.GetStatus();
        Assert.Equal(0, status.SuccessfulCycles);
        Assert.Equal(2, status.InterruptedCycles);
        Assert.Equal(0, status.BaselineContexts);
    }

    [Fact]
    public void Established_baseline_detects_slow_phase_and_slow_total_cycle()
    {
        var service = CreateService(minimumBaselineCycles: 3);
        var start = Utc(0);

        CompleteCycle(service, "BASE-1", start, movingToDestinationMs: 300);
        CompleteCycle(service, "BASE-2", start.AddSeconds(10), movingToDestinationMs: 305);
        CompleteCycle(service, "BASE-3", start.AddSeconds(20), movingToDestinationMs: 295);
        Assert.Empty(service.GetAnomalies());

        CompleteCycle(service, "SLOW", start.AddSeconds(30), movingToDestinationMs: 1000);

        var anomalies = service.GetAnomalies(10)
            .Where(anomaly => anomaly.RequestId == "SLOW")
            .OrderBy(anomaly => anomaly.Kind)
            .ToArray();
        Assert.Equal(2, anomalies.Length);
        var phase = Assert.Single(anomalies.Where(anomaly => anomaly.Kind == TransportCycleAnomalyKind.PhaseDuration));
        Assert.Equal(TransportExecutionState.MovingToDestination, phase.Phase);
        Assert.Equal(1000, phase.ActualMilliseconds);
        Assert.True(phase.Deviation > 60);
        var total = Assert.Single(anomalies.Where(anomaly => anomaly.Kind == TransportCycleAnomalyKind.TotalDuration));
        Assert.True(total.ActualMilliseconds > 1600);
        Assert.True(total.Deviation > 60);
        Assert.Equal(2, service.GetStatus().DurationAnomalies);
    }

    [Fact]
    public void Different_path_context_does_not_reuse_another_route_baseline()
    {
        var service = CreateService(minimumBaselineCycles: 3);
        var start = Utc(0);

        CompleteCycle(service, "A-1", start, 300, destination: "DST-A");
        CompleteCycle(service, "A-2", start.AddSeconds(10), 300, destination: "DST-A");
        CompleteCycle(service, "A-3", start.AddSeconds(20), 300, destination: "DST-A");
        CompleteCycle(service, "B-SLOW", start.AddSeconds(30), 1500, destination: "DST-B");

        Assert.Empty(service.GetAnomalies());
        Assert.Equal(2, service.GetStatus().BaselineContexts);
    }

    [Fact]
    public void Disabled_analysis_records_nothing()
    {
        var service = new TransportCycleAnalysisService(new TransportCycleAnalysisOptions { Enabled = false });
        var start = Utc(0);
        service.Observe(null, Snapshot("OFF", TransportExecutionState.Assigned, start, start), "Create", true);
        service.Observe(
            Snapshot("OFF", TransportExecutionState.Assigned, start, start),
            Snapshot("OFF", TransportExecutionState.Completed, start, start.AddSeconds(1)),
            "Complete",
            true);

        Assert.Empty(service.GetCycles());
        Assert.Empty(service.GetAnomalies());
        Assert.Equal(0, service.GetStatus().TrackedExecutions);
    }

    private static TransportCycleAnalysisService CreateService(int minimumBaselineCycles) =>
        new(new TransportCycleAnalysisOptions
        {
            Enabled = true,
            MinimumBaselineCycles = minimumBaselineCycles,
            MaximumBaselineCyclesPerContext = 100,
            MaximumTrackedExecutions = 1000,
            MaximumCompletedCycles = 1000,
            MaximumAnomalies = 1000,
            MadMultiplier = 6,
            MinimumMadMilliseconds = 10
        });

    private static void CompleteCycle(
        TransportCycleAnalysisService service,
        string requestId,
        DateTime start,
        double movingToDestinationMs,
        TransportExecutionState terminal = TransportExecutionState.Completed,
        string destination = "DST-A")
    {
        var assigned = Snapshot(requestId, TransportExecutionState.Assigned, start, start, destination: destination);
        service.Observe(null, assigned, "Create", true);

        var movingPickup = Snapshot(
            requestId,
            TransportExecutionState.MovingToPickup,
            start,
            start.AddMilliseconds(100),
            destination: destination);
        service.Observe(assigned, movingPickup, "Start", true);

        var loading = Snapshot(
            requestId,
            TransportExecutionState.Loading,
            start,
            start.AddMilliseconds(400),
            destination: destination);
        service.Observe(movingPickup, loading, "Position", true);

        var movingDestination = Snapshot(
            requestId,
            TransportExecutionState.MovingToDestination,
            start,
            start.AddMilliseconds(500),
            destination: destination);
        service.Observe(loading, movingDestination, "ConfirmLoaded", true);

        var unloadingAt = 500 + movingToDestinationMs;
        if (terminal == TransportExecutionState.Faulted)
        {
            var faulted = Snapshot(
                requestId,
                TransportExecutionState.Faulted,
                start,
                start.AddMilliseconds(unloadingAt),
                destination: destination,
                lastError: "simulated fault");
            service.Observe(movingDestination, faulted, "Fault", true);
            return;
        }

        var unloading = Snapshot(
            requestId,
            TransportExecutionState.Unloading,
            start,
            start.AddMilliseconds(unloadingAt),
            destination: destination);
        service.Observe(movingDestination, unloading, "Position", true);

        var completed = Snapshot(
            requestId,
            terminal,
            start,
            start.AddMilliseconds(unloadingAt + 200),
            destination: destination);
        service.Observe(unloading, completed, "ConfirmUnloaded", terminal == TransportExecutionState.Completed);
    }

    private static TransportExecutionSnapshot Snapshot(
        string requestId,
        TransportExecutionState state,
        DateTime createdAt,
        DateTime updatedAt,
        long feedback = -1,
        string destination = "DST-A",
        string? lastError = null)
    {
        var afterPickup = IsAtOrAfterPickup(state);
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
            LastFeedbackSequence = feedback,
            FullNodePath = new[] { "SRC", "PICK", destination },
            FullEdgePath = new[] { "E1", "E2" },
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt,
            LastError = lastError
        };
    }

    private static bool IsAtOrAfterPickup(TransportExecutionState state) => state is
        TransportExecutionState.Loading or
        TransportExecutionState.MovingToDestination or
        TransportExecutionState.Unloading or
        TransportExecutionState.Completed or
        TransportExecutionState.Faulted;

    private static DateTime Utc(int seconds) =>
        new DateTime(2026, 1, 1, 0, 0, seconds, DateTimeKind.Utc);

    private static void AssertPhase(
        TransportCyclePhaseDuration phase,
        TransportExecutionState state,
        double durationMs)
    {
        Assert.Equal(state, phase.State);
        Assert.Equal(durationMs, phase.DurationMilliseconds);
    }
}
