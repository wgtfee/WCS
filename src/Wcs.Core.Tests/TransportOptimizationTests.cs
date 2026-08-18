using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportOptimizationTests
{
    [Fact]
    public async Task Dispatch_ExcludesVehicleBelowMinimumBattery()
    {
        var runtime = CreateRuntime();
        runtime.Registry.Upsert(Vehicle("EMS-LOW", "N1", 10));
        runtime.Registry.Upsert(Vehicle("EMS-HIGH", "N4", 80));

        var result = await runtime.Dispatch.DispatchAsync(new TransportDispatchRequest
        {
            RequestId = "REQ-BATTERY",
            SourceNodeId = "N2",
            DestinationNodeId = "N3",
            MinimumBatteryPercent = 20
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Assignment);
        Assert.Equal("EMS-HIGH", result.Assignment!.VehicleId);
    }

    [Fact]
    public void ChargingCoordinator_ReservesCapacityAndPromotesQueue()
    {
        var runtime = CreateRuntime();
        runtime.Registry.Upsert(Vehicle("EMS-01", "N1", 20));
        runtime.Registry.Upsert(Vehicle("EMS-02", "N1", 15));

        var charging = new TransportChargingCoordinator(runtime.Registry, runtime.RouteCenter);
        charging.RegisterStation(new TransportChargingStationDefinition
        {
            StationId = "CH-01",
            NodeId = "C1",
            Name = "一号充电位",
            Capacity = 1,
            SupportedVehicleKinds = new[] { TransportVehicleKind.Ems }
        });

        var first = charging.EvaluateVehicle("EMS-01");
        var second = charging.EvaluateVehicle("EMS-02");

        Assert.True(first.PlanCreated);
        Assert.Equal(TransportChargingPlanState.Reserved, first.Plan!.State);
        Assert.True(second.PlanCreated);
        Assert.Equal(TransportChargingPlanState.WaitingForStation, second.Plan!.State);

        Assert.False(charging.ConfirmArrived(second.Plan.PlanId));
        Assert.True(charging.ConfirmArrived(first.Plan.PlanId));
        Assert.True(charging.Complete(first.Plan.PlanId, 90));

        var promoted = charging.GetPlans().Single(x => x.PlanId == second.Plan.PlanId);
        Assert.Equal(TransportChargingPlanState.Reserved, promoted.State);
        Assert.True(runtime.Registry.TryGet("EMS-01", out var chargedVehicle));
        Assert.Equal(TransportVehicleOperatingState.Idle, chargedVehicle!.State);
        Assert.Equal(90, chargedVehicle.BatteryPercent);
    }

    [Fact]
    public async Task Reassignment_BeforePickup_TransfersToHealthyVehicle()
    {
        var runtime = CreateRuntime();
        runtime.Registry.Upsert(Vehicle("EMS-01", "N1", 80));
        runtime.Registry.Upsert(Vehicle("EMS-02", "N4", 80));

        var dispatch = await runtime.Dispatch.DispatchAsync(new TransportDispatchRequest
        {
            RequestId = "REQ-TRANSFER",
            SourceNodeId = "N2",
            DestinationNodeId = "N3",
            Priority = 5
        });
        Assert.True(dispatch.Success);
        Assert.Equal("EMS-01", dispatch.Assignment!.VehicleId);

        Assert.True(runtime.Execution.Create("REQ-TRANSFER").Success);
        Assert.True(runtime.Execution.Start("REQ-TRANSFER").Success);

        var service = new TransportTaskReassignmentService(
            runtime.Dispatch,
            runtime.Execution,
            runtime.Registry,
            runtime.ExecutionControl);

        var result = await service.ReassignAsync("REQ-TRANSFER", "EMS-01 驱动故障");

        Assert.True(result.Success);
        Assert.Equal(TransportReassignmentDecision.Reassigned, result.Record.Decision);
        Assert.Equal("EMS-02", result.Record.ReplacementVehicleId);
        Assert.NotNull(result.Record.ReplacementRequestId);
        Assert.True(runtime.Registry.TryGet("EMS-01", out var failedVehicle));
        Assert.Equal(TransportVehicleOperatingState.Faulted, failedVehicle!.State);
        Assert.Equal(0, failedVehicle.ActiveTaskCount);
    }

    [Fact]
    public async Task Reassignment_AfterLoad_RequiresManualRecovery()
    {
        var runtime = CreateRuntime();
        runtime.Registry.Upsert(Vehicle("EMS-01", "N2", 80));
        runtime.Registry.Upsert(Vehicle("EMS-02", "N4", 80));

        var dispatch = await runtime.Dispatch.DispatchAsync(new TransportDispatchRequest
        {
            RequestId = "REQ-LOADED",
            SourceNodeId = "N2",
            DestinationNodeId = "N3",
            LoadId = "LOAD-01"
        });
        Assert.True(dispatch.Success);

        Assert.True(runtime.Execution.Create("REQ-LOADED").Success);
        Assert.Equal(TransportExecutionState.Loading, runtime.Execution.Start("REQ-LOADED").Snapshot!.State);
        Assert.Equal(
            TransportExecutionState.MovingToDestination,
            runtime.Execution.ConfirmLoaded("REQ-LOADED").Snapshot!.State);

        var service = new TransportTaskReassignmentService(
            runtime.Dispatch,
            runtime.Execution,
            runtime.Registry,
            runtime.ExecutionControl);

        var result = await service.ReassignAsync("REQ-LOADED", "车辆故障");

        Assert.False(result.Success);
        Assert.Equal(
            TransportReassignmentDecision.ManualRecoveryRequired,
            result.Record.Decision);
        Assert.True(runtime.Execution.TryGet("REQ-LOADED", out var original));
        Assert.Equal(TransportExecutionState.Faulted, original!.State);
        Assert.True(runtime.Registry.TryGet("EMS-01", out var failedVehicle));
        Assert.Equal(TransportVehicleOperatingState.Faulted, failedVehicle!.State);
    }

    [Fact]
    public void PerformanceSnapshot_ReportsChargingAndLowBatteryVehicles()
    {
        var runtime = CreateRuntime();
        runtime.Registry.Upsert(Vehicle("EMS-01", "N1", 25));
        runtime.Registry.Upsert(Vehicle("EMS-02", "N4", 90) with
        {
            State = TransportVehicleOperatingState.Executing,
            ActiveTaskCount = 1
        });

        var charging = new TransportChargingCoordinator(runtime.Registry, runtime.RouteCenter);
        charging.RegisterStation(new TransportChargingStationDefinition
        {
            StationId = "CH-01",
            NodeId = "C1",
            Capacity = 1
        });
        charging.EvaluateVehicle("EMS-01");

        var reassignments = new TransportTaskReassignmentService(
            runtime.Dispatch,
            runtime.Execution,
            runtime.Registry,
            runtime.ExecutionControl);
        var performance = new TransportPerformanceService(
            runtime.Registry,
            runtime.Execution,
            charging,
            reassignments);

        var snapshot = performance.GetSnapshot();

        Assert.Equal(2, snapshot.OnlineVehicleCount);
        Assert.Equal(1, snapshot.ChargingVehicleCount);
        Assert.Equal(1, snapshot.LowBatteryVehicleCount);
        Assert.Equal(100d, snapshot.FleetUtilizationPercent);
    }

    private static RuntimeFixture CreateRuntime()
    {
        var graph = new TopologyGraph();
        foreach (var nodeId in new[] { "N1", "N2", "N3", "N4", "C1" })
            graph.AddNode(new Node { NodeId = nodeId });

        graph.AddEdge(new Edge { EdgeId = "E12", FromNodeId = "N1", ToNodeId = "N2", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E42", FromNodeId = "N4", ToNodeId = "N2", Weight = 2 });
        graph.AddEdge(new Edge { EdgeId = "E23", FromNodeId = "N2", ToNodeId = "N3", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E1C", FromNodeId = "N1", ToNodeId = "C1", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E4C", FromNodeId = "N4", ToNodeId = "C1", Weight = 2 });

        var routeCenter = new TransportRouteCenter(graph);
        var registry = new InMemoryTransportVehicleRegistry();
        var selector = new DefaultTransportVehicleSelector(routeCenter);
        var reservations = new InMemoryRouteReservationManager(routeCenter);
        var dispatch = new UnifiedTransportDispatchEngine(
            registry,
            selector,
            routeCenter,
            reservations);
        var innerExecution = new InMemoryTransportExecutionEngine(
            dispatch,
            registry,
            reservations);
        var execution = new CoordinatedTransportExecutionEngine(innerExecution, registry);

        return new RuntimeFixture(
            routeCenter,
            registry,
            dispatch,
            execution,
            execution);
    }

    private static TransportVehicleSnapshot Vehicle(
        string vehicleId,
        string nodeId,
        int batteryPercent) => new()
    {
        VehicleId = vehicleId,
        Kind = TransportVehicleKind.Ems,
        State = TransportVehicleOperatingState.Idle,
        CurrentNodeId = nodeId,
        IsOnline = true,
        BatteryPercent = batteryPercent,
        Capabilities = TransportVehicleCapability.All,
        Version = 1
    };

    private sealed record RuntimeFixture(
        ITransportRouteCenter RouteCenter,
        InMemoryTransportVehicleRegistry Registry,
        UnifiedTransportDispatchEngine Dispatch,
        ITransportExecutionEngine Execution,
        ITransportReassignmentExecutionControl ExecutionControl);
}
