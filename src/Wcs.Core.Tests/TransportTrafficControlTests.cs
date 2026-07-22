using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportTrafficControlTests
{
    [Fact]
    public void IntersectionResource_AllowsOnlyOneConflictingOwner()
    {
        var traffic = CreateTraffic();
        traffic.RegisterRequest("REQ-A", "EMS-01", 10);
        traffic.RegisterRequest("REQ-B", "RGV-01", 5);

        var first = traffic.TryAcquire("REQ-A", new[] { "E-NORTH" }, TimeSpan.FromMinutes(1));
        var second = traffic.TryAcquire("REQ-B", new[] { "E-EAST" }, TimeSpan.FromMinutes(1));

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("REQ-A", second.BlockingOwnerIds);
        var wait = Assert.Single(traffic.GetWaits());
        Assert.Equal("REQ-B", wait.OwnerId);
        Assert.Contains("REQ-A", wait.BlockingOwnerIds);
    }

    [Fact]
    public void SameOwner_CanRenewSameTrafficResource()
    {
        var traffic = CreateTraffic();
        traffic.RegisterRequest("REQ-A", "EMS-01", 1);

        Assert.True(traffic.TryAcquire("REQ-A", new[] { "E-NORTH" }, TimeSpan.FromSeconds(10)).Success);
        Assert.True(traffic.TryAcquire("REQ-A", new[] { "E-EAST" }, TimeSpan.FromMinutes(1)).Success);
        Assert.Single(traffic.GetHolds());
    }

    [Fact]
    public void WaitForGraph_DetectsRealTwoOwnerCycle()
    {
        var traffic = CreateTwoResourceTraffic();
        CreateDeadlock(traffic);

        var cycle = Assert.Single(traffic.DetectDeadlocks());
        Assert.Contains("REQ-A", cycle.OwnerIds);
        Assert.Contains("REQ-B", cycle.OwnerIds);
        Assert.Contains("R1", cycle.ResourceIds);
        Assert.Contains("R2", cycle.ResourceIds);
    }

    [Fact]
    public void DeadlockResolver_SelectsLowerPriorityVictimAndBreaksCycle()
    {
        var traffic = CreateTwoResourceTraffic();
        CreateDeadlock(traffic);
        var service = new TransportDeadlockService(traffic);
        var cycle = Assert.Single(service.Detect());

        var result = service.Resolve(cycle.CycleId);

        Assert.Equal("REQ-B", result.VictimOwnerId);
        Assert.Equal(TransportDeadlockResolutionStatus.Resolved, result.Status);
        Assert.Contains("R2", result.ReleasedResourceIds);
        Assert.Empty(traffic.DetectDeadlocks());
    }

    [Fact]
    public void DeadlockResolver_DoesNotReleaseConfirmedPhysicalOccupancy()
    {
        var traffic = CreateTwoResourceTraffic();
        CreateDeadlock(traffic);
        Assert.True(traffic.MarkOccupancy("REQ-B", "R2", true));
        var service = new TransportDeadlockService(traffic);
        var cycle = Assert.Single(service.Detect());

        var result = service.Resolve(cycle.CycleId);

        Assert.Equal("REQ-B", result.VictimOwnerId);
        Assert.Equal(TransportDeadlockResolutionStatus.CycleBrokenAwaitingClearance, result.Status);
        Assert.Contains("R2", result.RetainedOccupiedResourceIds);
        Assert.Contains(traffic.GetHolds(), x => x.OwnerId == "REQ-B" && x.ResourceId == "R2" && x.OccupancyConfirmed);
    }

    [Fact]
    public void TrafficAwareReservation_BlocksConflictingRollingWindow()
    {
        var traffic = CreateTraffic();
        traffic.RegisterRequest("REQ-A", "EMS-01", 10);
        traffic.RegisterRequest("REQ-B", "RGV-01", 5);
        var routeCenter = new TransportRouteCenter(new TopologyGraph());
        var reservations = new TrafficAwareRouteReservationManager(
            new InMemoryRouteReservationManager(routeCenter),
            traffic);

        Assert.True(reservations.TryReserve("REQ-A", new[] { "E-NORTH" }, TimeSpan.FromMinutes(1), out var first));
        Assert.NotNull(first);
        Assert.False(reservations.TryReserve("REQ-B", new[] { "E-EAST" }, TimeSpan.FromMinutes(1), out _));
        Assert.Contains(traffic.GetWaits(), x => x.OwnerId == "REQ-B");

        Assert.True(reservations.Release(first!.ReservationId));
        Assert.True(reservations.TryReserve("REQ-B", new[] { "E-EAST" }, TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public async Task TrafficSnapshot_RestoresDefinitionsHoldsAndWaits()
    {
        var source = CreateTraffic();
        source.RegisterRequest("REQ-A", "EMS-01", 10);
        source.RegisterRequest("REQ-B", "RGV-01", 5);
        source.TryAcquire("REQ-A", new[] { "E-NORTH" }, TimeSpan.FromMinutes(1));
        source.TryAcquire("REQ-B", new[] { "E-EAST" }, TimeSpan.FromMinutes(1));
        var snapshot = await source.CaptureSnapshotAsync();

        var restored = new TransportTrafficCoordinator();
        await restored.RestoreSnapshotAsync(snapshot);

        Assert.Single(restored.GetResources());
        Assert.Single(restored.GetHolds());
        Assert.Single(restored.GetWaits());
    }

    private static TransportTrafficCoordinator CreateTraffic()
    {
        var traffic = new TransportTrafficCoordinator();
        traffic.RegisterResource(new TransportTrafficResourceDefinition
        {
            ResourceId = "X-01",
            Name = "一号交叉口",
            Kind = TransportTrafficResourceKind.Intersection,
            EdgeIds = new[] { "E-NORTH", "E-EAST" },
            Capacity = 1
        });
        return traffic;
    }

    private static TransportTrafficCoordinator CreateTwoResourceTraffic()
    {
        var traffic = new TransportTrafficCoordinator();
        traffic.RegisterResource(new TransportTrafficResourceDefinition
        {
            ResourceId = "R1",
            Name = "单轨区一",
            Kind = TransportTrafficResourceKind.SingleTrack,
            EdgeIds = new[] { "E1" }
        });
        traffic.RegisterResource(new TransportTrafficResourceDefinition
        {
            ResourceId = "R2",
            Name = "单轨区二",
            Kind = TransportTrafficResourceKind.SingleTrack,
            EdgeIds = new[] { "E2" }
        });
        return traffic;
    }

    private static void CreateDeadlock(TransportTrafficCoordinator traffic)
    {
        traffic.RegisterRequest("REQ-A", "EMS-01", 10);
        traffic.RegisterRequest("REQ-B", "RGV-01", 1);
        Assert.True(traffic.TryAcquire("REQ-A", new[] { "E1" }, TimeSpan.FromMinutes(1)).Success);
        Assert.True(traffic.TryAcquire("REQ-B", new[] { "E2" }, TimeSpan.FromMinutes(1)).Success);
        Assert.False(traffic.TryAcquire("REQ-A", new[] { "E2" }, TimeSpan.FromMinutes(1)).Success);
        Assert.False(traffic.TryAcquire("REQ-B", new[] { "E1" }, TimeSpan.FromMinutes(1)).Success);
    }
}
