using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportExecutionEngineTests
{
    [Fact]
    public async Task Dispatch_ReservesOnlyInitialRollingWindow()
    {
        var fixture = CreateFixture();

        var result = await fixture.Dispatch.DispatchAsync(CreateRequest());

        Assert.True(result.Success);
        var reservation = Assert.Single(fixture.Reservations.GetActiveReservations());
        Assert.Equal(new[] { "E1", "E2" }, reservation.EdgeIds);
    }

    [Fact]
    public async Task PositionFeedback_ReleasesPassedEdgeAndExtendsWindow()
    {
        var fixture = CreateFixture();
        var dispatch = await fixture.Dispatch.DispatchAsync(CreateRequest());
        Assert.True(dispatch.Success);

        var started = fixture.Execution.Start("TASK-200");
        Assert.True(started.Success);
        Assert.Equal(TransportExecutionState.MovingToPickup, started.Snapshot!.State);

        var moved = fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N2",
            Sequence = 1
        });

        Assert.True(moved.Success);
        Assert.Equal("N2", moved.Snapshot!.CurrentNodeId);
        Assert.Equal(new[] { "E2", "E3" }, moved.Snapshot.ActiveReservedEdges);
        Assert.Equal(new[] { "E2", "E3" }, Assert.Single(fixture.Reservations.GetActiveReservations()).EdgeIds);
    }

    [Fact]
    public async Task PositionFeedback_RejectsDuplicateOrOutOfOrderSequence()
    {
        var fixture = CreateFixture();
        Assert.True((await fixture.Dispatch.DispatchAsync(CreateRequest())).Success);
        Assert.True(fixture.Execution.Start("TASK-200").Success);

        Assert.True(fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N2",
            Sequence = 5
        }).Success);

        var stale = fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N3",
            Sequence = 4
        });

        Assert.False(stale.Success);
        Assert.Contains("乱序", stale.FailureReason);
        Assert.Equal("N2", stale.Snapshot!.CurrentNodeId);
    }

    [Fact]
    public async Task PositionFeedback_WaitsWhenNextBlockCannotBeReserved()
    {
        var fixture = CreateFixture();
        Assert.True((await fixture.Dispatch.DispatchAsync(CreateRequest())).Success);
        Assert.True(fixture.Execution.Start("TASK-200").Success);

        Assert.True(fixture.Reservations.TryReserve(
            "OTHER-TASK",
            new[] { "E3" },
            TimeSpan.FromMinutes(1),
            out _));

        var blocked = fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N2",
            Sequence = 1
        });

        Assert.False(blocked.Success);
        Assert.Equal(TransportExecutionState.WaitingForRoute, blocked.Snapshot!.State);
        Assert.Contains("闭塞", blocked.Snapshot.LastError);
    }

    [Fact]
    public async Task Execution_CompletesLoadMoveUnloadLifecycle()
    {
        var fixture = CreateFixture();
        Assert.True((await fixture.Dispatch.DispatchAsync(CreateRequest())).Success);
        Assert.True(fixture.Execution.Start("TASK-200").Success);

        Assert.True(fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N4",
            Sequence = 1
        }).Success);

        Assert.True(fixture.Execution.TryGet("TASK-200", out var loading));
        Assert.Equal(TransportExecutionState.Loading, loading!.State);

        var loaded = fixture.Execution.ConfirmLoaded("TASK-200");
        Assert.True(loaded.Success);
        Assert.Equal(TransportExecutionState.MovingToDestination, loaded.Snapshot!.State);

        var arrived = fixture.Execution.ApplyPositionFeedback(new TransportPositionFeedback
        {
            VehicleId = "EMS-01",
            NodeId = "N6",
            Sequence = 2
        });
        Assert.True(arrived.Success);
        Assert.Equal(TransportExecutionState.Unloading, arrived.Snapshot!.State);

        var completed = fixture.Execution.ConfirmUnloaded("TASK-200");
        Assert.True(completed.Success);
        Assert.Equal(TransportExecutionState.Completed, completed.Snapshot!.State);
        Assert.Empty(fixture.Reservations.GetActiveReservations());

        Assert.True(fixture.Registry.TryGet("EMS-01", out var vehicle));
        Assert.Equal(TransportVehicleOperatingState.Idle, vehicle!.State);
    }

    [Fact]
    public async Task PauseAndResume_EmitLogicalStopAndMoveCommands()
    {
        var fixture = CreateFixture();
        Assert.True((await fixture.Dispatch.DispatchAsync(CreateRequest())).Success);
        Assert.True(fixture.Execution.Start("TASK-200").Success);

        var initialCommands = fixture.Execution.DequeueCommands("EMS-01");
        Assert.Contains(initialCommands, x =>
            x.CommandType == TransportExecutionCommandType.MoveToNode &&
            x.TargetNodeId == "N2");

        var paused = fixture.Execution.Pause("TASK-200");
        Assert.True(paused.Success);
        Assert.Equal(TransportExecutionState.Paused, paused.Snapshot!.State);
        Assert.Contains(
            fixture.Execution.DequeueCommands("EMS-01"),
            x => x.CommandType == TransportExecutionCommandType.Stop);

        var resumed = fixture.Execution.Resume("TASK-200");
        Assert.True(resumed.Success);
        Assert.Equal(TransportExecutionState.MovingToPickup, resumed.Snapshot!.State);
        Assert.Contains(
            fixture.Execution.DequeueCommands("EMS-01"),
            x => x.CommandType == TransportExecutionCommandType.MoveToNode);
    }

    private static Fixture CreateFixture()
    {
        var graph = new TopologyGraph();
        for (var i = 1; i <= 6; i++)
            graph.AddNode(new Node { NodeId = $"N{i}" });

        for (var i = 1; i <= 5; i++)
        {
            graph.AddEdge(new Edge
            {
                EdgeId = $"E{i}",
                FromNodeId = $"N{i}",
                ToNodeId = $"N{i + 1}",
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

        var execution = new InMemoryTransportExecutionEngine(
            dispatch,
            registry,
            reservations);

        return new Fixture(registry, reservations, dispatch, execution);
    }

    private static TransportDispatchRequest CreateRequest() => new()
    {
        RequestId = "TASK-200",
        SourceNodeId = "N4",
        DestinationNodeId = "N6",
        RequiredCapability = TransportVehicleCapability.Carry,
        RequiredEdgeCapability = EdgeCapability.Transport,
        RouteStrategy = TransportRouteStrategy.Shortest,
        ReservationWindowEdges = 2,
        ReservationLease = TimeSpan.FromMinutes(1)
    };

    private sealed record Fixture(
        InMemoryTransportVehicleRegistry Registry,
        InMemoryRouteReservationManager Reservations,
        UnifiedTransportDispatchEngine Dispatch,
        InMemoryTransportExecutionEngine Execution);
}
