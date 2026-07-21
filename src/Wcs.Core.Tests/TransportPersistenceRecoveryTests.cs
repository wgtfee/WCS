using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportPersistenceRecoveryTests
{
    [Fact]
    public async Task StateStore_RoundTripsRuntimeState()
    {
        var store = new InMemoryTransportStateStore();
        var vehicle = new TransportVehicleSnapshot
        {
            VehicleId = "RGV-01",
            Kind = TransportVehicleKind.Rgv,
            State = TransportVehicleOperatingState.Executing,
            CurrentNodeId = "N1",
            IsOnline = true,
            Version = 1
        };
        var execution = new TransportExecutionSnapshot
        {
            RequestId = "REQ-01",
            VehicleId = vehicle.VehicleId,
            CurrentNodeId = "N1",
            FullNodePath = new[] { "N1", "N2" },
            FullEdgePath = new[] { "E1" },
            State = TransportExecutionState.MovingToDestination
        };

        await store.SaveVehicleAsync(vehicle);
        await store.SaveExecutionAsync(execution);

        var snapshot = await store.LoadAsync();
        Assert.Single(snapshot.Vehicles);
        Assert.Single(snapshot.Executions);
        Assert.Equal("REQ-01", snapshot.Executions[0].RequestId);
    }

    [Fact]
    public async Task CommandDispatcher_PersistsCompletedCommand()
    {
        var store = new InMemoryTransportStateStore();
        var drivers = new ITransportVehicleDriver[]
        {
            new SimulatorTransportVehicleDriver(TransportVehicleKind.Rgv)
        };
        var dispatcher = new TransportCommandDispatcher(new TransportDriverResolver(drivers), store);
        var command = new TransportExecutionCommand
        {
            RequestId = "REQ-02",
            VehicleId = "RGV-02",
            CommandType = TransportExecutionCommandType.MoveToNode,
            TargetNodeId = "N2"
        };

        var result = await dispatcher.DispatchAsync(command, TransportVehicleKind.Rgv);
        var snapshot = await store.LoadAsync();

        Assert.Equal(TransportCommandStatus.Completed, result.Status);
        Assert.Single(snapshot.Commands);
        Assert.Equal(command.CommandId, snapshot.Commands[0].CommandId);
    }

    [Fact]
    public async Task Recovery_PositionMismatch_RequiresManualConfirmation()
    {
        var store = new InMemoryTransportStateStore();
        await store.SaveVehicleAsync(new TransportVehicleSnapshot
        {
            VehicleId = "EMS-01",
            Kind = TransportVehicleKind.Ems,
            State = TransportVehicleOperatingState.Executing,
            CurrentNodeId = "N1",
            IsOnline = true,
            Version = 1
        });
        await store.SaveExecutionAsync(new TransportExecutionSnapshot
        {
            RequestId = "REQ-03",
            VehicleId = "EMS-01",
            CurrentNodeId = "N1",
            FullNodePath = new[] { "N1", "N2" },
            FullEdgePath = new[] { "E1" },
            State = TransportExecutionState.MovingToDestination
        });

        var coordinator = new TransportRecoveryCoordinator(
            store,
            new TransportDriverResolver(new[]
            {
                new SimulatorTransportVehicleDriver(TransportVehicleKind.Ems)
            }));

        var report = await coordinator.RecoverAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(TransportRecoveryDecision.PositionMismatch, item.Decision);
        Assert.Equal(1, report.ManualConfirmationCount);
    }
}
