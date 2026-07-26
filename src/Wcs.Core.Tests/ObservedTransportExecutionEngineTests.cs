namespace Wcs.Core.Tests;

using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;
using Wcs.Core.TransportScheduling;

public sealed class ObservedTransportExecutionEngineTests
{
    [Fact]
    public async Task Direct_start_preserves_assigned_phase_and_records_complete_real_execution_cycle()
    {
        var fixture = CreateFixture(enabled: true);
        Assert.True((await fixture.Dispatch.DispatchAsync(CreateRequest("OBS-COMPLETE"))).Success);

        var started = fixture.Execution.Start("OBS-COMPLETE");
        Assert.True(started.Success);
        Assert.Equal(TransportExecutionState.MovingToPickup, started.Snapshot!.State);

        Assert.True(fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N4",
            Sequence = 1
        }).Success);
        Assert.True(fixture.Execution.ConfirmLoaded("OBS-COMPLETE").Success);
        Assert.True(fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N6",
            Sequence = 2
        }).Success);
        Assert.True(fixture.Execution.ConfirmUnloaded("OBS-COMPLETE").Success);

        var cycle = Assert.Single(fixture.Analysis.GetCycles());
        Assert.True(cycle.IsSuccessful);
        Assert.True(cycle.IsSequenceValid);
        Assert.Collection(
            cycle.Phases,
            phase => Assert.Equal(TransportExecutionState.Assigned, phase.State),
            phase => Assert.Equal(TransportExecutionState.MovingToPickup, phase.State),
            phase => Assert.Equal(TransportExecutionState.Loading, phase.State),
            phase => Assert.Equal(TransportExecutionState.MovingToDestination, phase.State),
            phase => Assert.Equal(TransportExecutionState.Unloading, phase.State));
        Assert.Equal(0, fixture.Analysis.GetStatus().TrackedExecutions);
        Assert.Equal(1, fixture.Analysis.GetStatus().SuccessfulCycles);
    }

    [Fact]
    public async Task Fault_reassignment_is_observed_once_and_post_terminal_retry_does_not_duplicate_cycle()
    {
        var fixture = CreateFixture(enabled: true);
        Assert.True((await fixture.Dispatch.DispatchAsync(CreateRequest("OBS-REASSIGN"))).Success);
        Assert.True(fixture.Execution.Start("OBS-REASSIGN").Success);

        var reassigned = fixture.Execution.FaultAndPrepareForReassignment(
            "OBS-REASSIGN",
            "simulated vehicle fault");
        Assert.True(reassigned.Success);
        Assert.Equal(TransportExecutionState.Cancelled, reassigned.Snapshot!.State);

        var firstCycle = Assert.Single(fixture.Analysis.GetCycles());
        Assert.Equal(TransportExecutionState.Cancelled, firstCycle.TerminalState);
        Assert.False(firstCycle.IsSuccessful);
        Assert.Equal(1, fixture.Analysis.GetStatus().InterruptedCycles);
        Assert.Equal(0, fixture.Analysis.GetStatus().TrackedExecutions);

        var repeated = fixture.Execution.FaultAndPrepareForReassignment(
            "OBS-REASSIGN",
            "repeated fault request");
        Assert.False(repeated.Success);
        Assert.Single(fixture.Analysis.GetCycles());
        Assert.Equal(1, fixture.Analysis.GetStatus().InterruptedCycles);
        Assert.Equal(0, fixture.Analysis.GetStatus().TrackedExecutions);
    }

    [Fact]
    public async Task Disabled_observer_is_zero_overhead_for_analysis_state()
    {
        var fixture = CreateFixture(enabled: false);
        Assert.True((await fixture.Dispatch.DispatchAsync(CreateRequest("OBS-OFF"))).Success);
        Assert.True(fixture.Execution.Start("OBS-OFF").Success);

        Assert.Empty(fixture.Analysis.GetCycles());
        Assert.Empty(fixture.Analysis.GetAnomalies());
        Assert.Equal(0, fixture.Analysis.GetStatus().TrackedExecutions);
        Assert.Equal(TransportExecutionState.MovingToPickup, fixture.Execution.GetAll().Single().State);
    }

    private static Fixture CreateFixture(bool enabled)
    {
        var graph = new TopologyGraph();
        for (var index = 1; index <= 6; index++)
            graph.AddNode(new Node { NodeId = $"N{index}" });
        for (var index = 1; index <= 5; index++)
        {
            graph.AddEdge(new Edge
            {
                EdgeId = $"E{index}",
                FromNodeId = $"N{index}",
                ToNodeId = $"N{index + 1}",
                Weight = 1,
                Capability = EdgeCapability.Transport
            });
        }

        var routeCenter = new TransportRouteCenter(graph);
        var registry = new InMemoryTransportVehicleRegistry();
        registry.Upsert(new TransportVehicleSnapshot
        {
            VehicleId = "EMS-01",
            Kind = TransportVehicleKind.Ems,
            State = TransportVehicleOperatingState.Idle,
            CurrentNodeId = "N1",
            IsOnline = true,
            BatteryPercent = 90,
            Capabilities = TransportVehicleCapability.Carry,
            Version = 1
        });
        var reservations = new InMemoryRouteReservationManager(routeCenter);
        var dispatch = new UnifiedTransportDispatchEngine(
            registry,
            new DefaultTransportVehicleSelector(routeCenter),
            routeCenter,
            reservations);
        var inner = new InMemoryTransportExecutionEngine(dispatch, registry, reservations);
        var coordinated = new CoordinatedTransportExecutionEngine(inner, registry);
        var options = new TransportCycleAnalysisOptions
        {
            Enabled = enabled,
            MinimumBaselineCycles = 3,
            MaximumBaselineCyclesPerContext = 100,
            MaximumTrackedExecutions = 100,
            MaximumCompletedCycles = 100,
            MaximumAnomalies = 100
        };
        var analysis = new TransportCycleAnalysisService(options);
        var observed = new ObservedTransportExecutionEngine(coordinated, analysis, options);
        return new Fixture(dispatch, observed, analysis);
    }

    private static TransportDispatchRequest CreateRequest(string requestId) => new()
    {
        RequestId = requestId,
        SourceNodeId = "N4",
        DestinationNodeId = "N6",
        RequiredCapability = TransportVehicleCapability.Carry,
        RequiredEdgeCapability = EdgeCapability.Transport,
        RouteStrategy = TransportRouteStrategy.Shortest,
        ReservationWindowEdges = 2,
        ReservationLease = TimeSpan.FromMinutes(1)
    };

    private sealed record Fixture(
        UnifiedTransportDispatchEngine Dispatch,
        ObservedTransportExecutionEngine Execution,
        TransportCycleAnalysisService Analysis);
}
