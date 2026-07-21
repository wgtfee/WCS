using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class UnifiedTransportDispatchEngineTests
{
    [Fact]
    public void VehicleRegistry_RejectsStaleSnapshot()
    {
        var registry = new InMemoryTransportVehicleRegistry();
        var current = CreateVehicle("EMS-01", TransportVehicleKind.Ems, "N1") with
        {
            Version = 2,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var stale = current with
        {
            CurrentNodeId = "N2",
            Version = 1,
            UpdatedAtUtc = current.UpdatedAtUtc.AddSeconds(1)
        };

        Assert.True(registry.Upsert(current));
        Assert.False(registry.Upsert(stale));
        Assert.True(registry.TryGet("EMS-01", out var saved));
        Assert.Equal("N1", saved!.CurrentNodeId);
    }

    [Fact]
    public void VehicleRegistry_FiltersVehicleKindAndCapability()
    {
        var registry = new InMemoryTransportVehicleRegistry();
        registry.Upsert(CreateVehicle("EMS-01", TransportVehicleKind.Ems, "N1") with
        {
            Capabilities = TransportVehicleCapability.Carry | TransportVehicleCapability.Lift
        });
        registry.Upsert(CreateVehicle("RGV-01", TransportVehicleKind.Rgv, "N2") with
        {
            Capabilities = TransportVehicleCapability.Carry
        });

        var available = registry.GetAvailable(CreateRequest() with
        {
            RequiredCapability = TransportVehicleCapability.Lift,
            AllowedVehicleKinds = new HashSet<TransportVehicleKind> { TransportVehicleKind.Ems }
        });

        var vehicle = Assert.Single(available);
        Assert.Equal("EMS-01", vehicle.VehicleId);
    }

    [Fact]
    public void VehicleSelector_RanksNearestVehicleFirst()
    {
        var routeCenter = CreateRouteCenter();
        var selector = new DefaultTransportVehicleSelector(routeCenter);
        var request = CreateRequest();

        var vehicles = new[]
        {
            CreateVehicle("EMS-01", TransportVehicleKind.Ems, "N1"),
            CreateVehicle("RGV-01", TransportVehicleKind.Rgv, "N3")
        };

        var candidates = selector.RankCandidates(request, vehicles);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("RGV-01", candidates[0].Vehicle.VehicleId);
    }

    [Fact]
    public void ReservationManager_ReservesAllOrNothing()
    {
        var routeCenter = CreateRouteCenter();
        var manager = new InMemoryRouteReservationManager(routeCenter);

        Assert.True(manager.TryReserve("TASK-1", new[] { "E1", "E2" }, TimeSpan.FromMinutes(1), out var first));
        Assert.NotNull(first);

        Assert.False(manager.TryReserve("TASK-2", new[] { "E2", "E3" }, TimeSpan.FromMinutes(1), out var second));
        Assert.Null(second);

        Assert.True(manager.Release(first!.ReservationId));
        Assert.True(manager.TryReserve("TASK-2", new[] { "E2", "E3" }, TimeSpan.FromMinutes(1), out second));
        Assert.NotNull(second);
    }

    [Fact]
    public void ReservationManager_CleansExpiredLease()
    {
        var routeCenter = CreateRouteCenter();
        var manager = new InMemoryRouteReservationManager(routeCenter);

        Assert.True(manager.TryReserve("TASK-1", new[] { "E1" }, TimeSpan.FromMilliseconds(10), out var reservation));
        Assert.NotNull(reservation);

        var cleaned = manager.CleanupExpired(reservation!.ExpiresAtUtc.AddMilliseconds(1));

        Assert.Equal(1, cleaned);
        Assert.Empty(manager.GetActiveReservations());
        Assert.True(manager.TryReserve("TASK-2", new[] { "E1" }, TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public async Task DispatchAsync_ReturnsFailureWhenNoVehicleMatches()
    {
        var routeCenter = CreateRouteCenter();
        var registry = new InMemoryTransportVehicleRegistry();
        registry.Upsert(CreateVehicle("RGV-01", TransportVehicleKind.Rgv, "N1"));

        var engine = new UnifiedTransportDispatchEngine(
            registry,
            new DefaultTransportVehicleSelector(routeCenter),
            routeCenter,
            new InMemoryRouteReservationManager(routeCenter));

        var result = await engine.DispatchAsync(CreateRequest() with
        {
            AllowedVehicleKinds = new HashSet<TransportVehicleKind> { TransportVehicleKind.Ems }
        });

        Assert.False(result.Success);
        Assert.Null(result.Assignment);
        Assert.Contains("没有满足", result.FailureReason);
    }

    [Fact]
    public async Task DispatchAsync_IsIdempotentAndCompletesReservation()
    {
        var routeCenter = CreateRouteCenter();
        var registry = new InMemoryTransportVehicleRegistry();
        registry.Upsert(CreateVehicle("EMS-01", TransportVehicleKind.Ems, "N1"));

        var reservations = new InMemoryRouteReservationManager(routeCenter);
        var engine = new UnifiedTransportDispatchEngine(
            registry,
            new DefaultTransportVehicleSelector(routeCenter),
            routeCenter,
            reservations);

        var request = CreateRequest() with { RequestId = "TASK-100" };

        var first = await engine.DispatchAsync(request);
        var second = await engine.DispatchAsync(request);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotNull(first.Assignment);
        Assert.Equal(first.Assignment, second.Assignment);
        Assert.Single(reservations.GetActiveReservations());
        Assert.True(registry.TryGet("EMS-01", out var executingVehicle));
        Assert.Equal(TransportVehicleOperatingState.Executing, executingVehicle!.State);

        Assert.True(engine.Complete(request.RequestId));
        Assert.Empty(reservations.GetActiveReservations());
        Assert.True(registry.TryGet("EMS-01", out var idleVehicle));
        Assert.Equal(TransportVehicleOperatingState.Idle, idleVehicle!.State);
        Assert.Equal(0, idleVehicle.ActiveTaskCount);
    }

    private static TransportDispatchRequest CreateRequest() => new()
    {
        RequestId = "TASK-1",
        SourceNodeId = "N4",
        DestinationNodeId = "N5",
        RequiredCapability = TransportVehicleCapability.Carry,
        RequiredEdgeCapability = EdgeCapability.Transport,
        RouteStrategy = TransportRouteStrategy.Shortest
    };

    private static TransportVehicleSnapshot CreateVehicle(
        string vehicleId,
        TransportVehicleKind kind,
        string currentNodeId) => new()
    {
        VehicleId = vehicleId,
        Kind = kind,
        State = TransportVehicleOperatingState.Idle,
        CurrentNodeId = currentNodeId,
        IsOnline = true,
        BatteryPercent = 80,
        Capabilities = TransportVehicleCapability.Carry,
        Version = 1
    };

    private static ITransportRouteCenter CreateRouteCenter()
    {
        var graph = new TopologyGraph();
        for (var i = 1; i <= 5; i++)
            graph.AddNode(new Node { NodeId = $"N{i}" });

        graph.AddEdge(new Edge { EdgeId = "E1", FromNodeId = "N1", ToNodeId = "N2", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E2", FromNodeId = "N2", ToNodeId = "N3", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E3", FromNodeId = "N3", ToNodeId = "N4", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E4", FromNodeId = "N4", ToNodeId = "N5", Weight = 1 });

        return new TransportRouteCenter(graph);
    }
}
