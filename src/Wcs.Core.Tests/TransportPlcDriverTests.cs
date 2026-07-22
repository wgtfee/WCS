using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportPlcDriverTests
{
    [Fact]
    public async Task PlcChannel_WritesPayloadBeforeRequestAndCorrelatesAcknowledgement()
    {
        var fixture = CreateFixture();
        var command = new TransportProtocolCommandFrame
        {
            CommandId = "CMD-01",
            RequestId = "REQ-01",
            VehicleId = "EMS-01",
            Sequence = 7,
            CommandType = TransportExecutionCommandType.MoveToNode,
            TargetNodeId = "N2"
        };

        await fixture.Channel.WriteCommandAsync(command);

        Assert.Equal(7L, Convert.ToInt64(fixture.Accessor.GetValue("DRV-01", "cmd.seq")));
        Assert.Equal(101, Convert.ToInt32(fixture.Accessor.GetValue("DRV-01", "cmd.code")));
        Assert.Equal(20, Convert.ToInt32(fixture.Accessor.GetValue("DRV-01", "cmd.target")));
        Assert.Equal(true, fixture.Accessor.GetValue("DRV-01", "cmd.request"));

        fixture.Accessor.SetValue("DRV-01", "ack.seq", 7L);
        fixture.Accessor.SetValue("DRV-01", "ack.accepted", true);
        fixture.Accessor.SetValue("DRV-01", "ack.completed", true);
        var state = await fixture.Channel.ReadStateAsync("EMS-01");

        Assert.Equal("CMD-01", state.AcknowledgedCommandId);
        Assert.True(state.CommandAccepted);
        Assert.True(state.CommandCompleted);
        Assert.Equal(false, fixture.Accessor.GetValue("DRV-01", "cmd.request"));
    }

    [Fact]
    public async Task PlcChannel_HeartbeatStopsChanging_MarksVehicleOffline()
    {
        var fixture = CreateFixture(heartbeatTimeoutMs: 5);
        var first = await fixture.Channel.ReadStateAsync("EMS-01");
        Assert.True(first.DeviceOnline);

        await Task.Delay(20);
        var stale = await fixture.Channel.ReadStateAsync("EMS-01");

        Assert.False(stale.DeviceOnline);
        Assert.Equal(TransportVehicleOperatingState.Offline, stale.OperatingState);
    }

    [Fact]
    public async Task ReliableDriver_WaitsForMatchingSequenceAndCompletes()
    {
        var fixture = CreateFixture();
        var driver = new ReliableTransportVehicleDriver(
            TransportVehicleKind.Ems,
            fixture.Channel,
            new ReliableTransportVehicleDriverOptions
            {
                HeartbeatTimeout = TimeSpan.FromSeconds(1),
                CommandAcknowledgementTimeout = TimeSpan.FromSeconds(1),
                PollInterval = TimeSpan.FromMilliseconds(5)
            });
        var command = new TransportExecutionCommand
        {
            CommandId = "CMD-RELIABLE",
            RequestId = "REQ-RELIABLE",
            VehicleId = "EMS-01",
            CommandType = TransportExecutionCommandType.MoveToNode,
            TargetNodeId = "N2"
        };

        var sending = driver.SendCommandAsync(command);
        for (var i = 0; i < 100 && fixture.Accessor.GetValue("DRV-01", "cmd.request") is not true; i++)
            await Task.Delay(2);

        var sequence = Convert.ToInt64(fixture.Accessor.GetValue("DRV-01", "cmd.seq"));
        fixture.Accessor.SetValue("DRV-01", "heartbeat", 2L);
        fixture.Accessor.SetValue("DRV-01", "ack.seq", sequence);
        fixture.Accessor.SetValue("DRV-01", "ack.accepted", true);
        fixture.Accessor.SetValue("DRV-01", "ack.completed", true);

        var result = await sending;

        Assert.True(result.Accepted);
        Assert.True(result.Completed);
    }

    [Fact]
    public async Task Synchronization_UpdatesVehiclePositionBatteryAndState()
    {
        var fixture = CreateFixture();
        fixture.Accessor.SetValue("DRV-01", "node", 20);
        fixture.Accessor.SetValue("DRV-01", "state", 2);
        fixture.Accessor.SetValue("DRV-01", "battery", 64);
        fixture.Accessor.SetValue("DRV-01", "state.seq", 3L);

        var vehicles = new InMemoryTransportVehicleRegistry();
        var resolver = CreateResolver(fixture.Registry, fixture.Channel);
        var store = new InMemoryTransportStateStore();
        var sync = new TransportDriverSynchronizationService(
            fixture.Registry,
            resolver,
            vehicles,
            store);

        var report = await sync.PollAllAsync();

        Assert.Equal(1, report.UpdatedCount);
        Assert.True(vehicles.TryGet("EMS-01", out var vehicle));
        Assert.Equal("N2", vehicle!.CurrentNodeId);
        Assert.Equal(64, vehicle.BatteryPercent);
        Assert.Equal(TransportVehicleOperatingState.Executing, vehicle.State);
    }

    [Fact]
    public async Task Reconciliation_PositionMismatch_RequiresManualConfirmation()
    {
        var fixture = CreateFixture();
        fixture.Accessor.SetValue("DRV-01", "node", 20);
        var vehicles = new InMemoryTransportVehicleRegistry();
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
        var sync = new TransportDriverSynchronizationService(
            fixture.Registry,
            CreateResolver(fixture.Registry, fixture.Channel),
            vehicles,
            store);

        var report = await sync.ReconcileAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(TransportDriverReconciliationDecision.PositionMismatch, item.Decision);
        Assert.Equal(1, report.ManualConfirmationCount);
    }

    [Fact]
    public async Task SignalMapStore_RejectsStaleExpectedVersion()
    {
        var store = new InMemoryTransportPlcSignalMapStore();
        var registry = new InMemoryTransportPlcSignalMapRegistry();
        var service = new TransportPlcSignalMapService(store, registry);
        var map = CreateMap();

        var first = await service.SaveAndApplyAsync(map, 0, "tester");
        var stale = await service.SaveAndApplyAsync(map with { PollIntervalMs = 500 }, 0, "tester-2");

        Assert.True(first.Success);
        Assert.True(stale.VersionConflict);
        Assert.True(registry.TryGet("EMS-01", out var applied));
        Assert.Equal(1, applied!.Version);
        Assert.Equal(200, applied.PollIntervalMs);
    }

    private static DriverFixture CreateFixture(int heartbeatTimeoutMs = 1000)
    {
        var registry = new InMemoryTransportPlcSignalMapRegistry();
        registry.Upsert(CreateMap(heartbeatTimeoutMs));
        var accessor = new InMemoryTransportPlcAccessor();
        accessor.SetConnected("DRV-01", true);
        accessor.SetValue("DRV-01", "heartbeat", 1L);
        accessor.SetValue("DRV-01", "online", true);
        accessor.SetValue("DRV-01", "node", 10);
        accessor.SetValue("DRV-01", "state", 1);
        accessor.SetValue("DRV-01", "battery", 80);
        accessor.SetValue("DRV-01", "fault", 0);
        accessor.SetValue("DRV-01", "state.seq", 1L);
        accessor.SetValue("DRV-01", "ack.seq", 0L);
        accessor.SetValue("DRV-01", "ack.accepted", false);
        accessor.SetValue("DRV-01", "ack.completed", false);
        var diagnostics = new TransportDriverDiagnosticsService();
        var channel = new TransportPlcDriverChannel(registry, accessor, diagnostics);
        return new DriverFixture(registry, accessor, diagnostics, channel);
    }

    private static TransportPlcSignalMap CreateMap(int heartbeatTimeoutMs = 1000) => new()
    {
        DriverId = "DRV-01",
        VehicleId = "EMS-01",
        Kind = TransportVehicleKind.Ems,
        Mode = TransportDriverMode.PlcTag,
        Enabled = true,
        PollIntervalMs = 200,
        HeartbeatTimeoutMs = heartbeatTimeoutMs,
        HeartbeatTag = "heartbeat",
        DeviceOnlineTag = "online",
        CurrentNodeTag = "node",
        OperatingStateTag = "state",
        BatteryPercentTag = "battery",
        FaultCodeTag = "fault",
        StateSequenceTag = "state.seq",
        CommandSequenceTag = "cmd.seq",
        CommandCodeTag = "cmd.code",
        TargetNodeTag = "cmd.target",
        CommandRequestTag = "cmd.request",
        AcknowledgedSequenceTag = "ack.seq",
        CommandAcceptedTag = "ack.accepted",
        CommandCompletedTag = "ack.completed",
        NodeCodeMap = new Dictionary<int, string> { [10] = "N1", [20] = "N2" },
        TargetNodeCodeMap = new Dictionary<string, int>(StringComparer.Ordinal) { ["N1"] = 10, ["N2"] = 20 },
        OperatingStateMap = new Dictionary<int, TransportVehicleOperatingState>
        {
            [0] = TransportVehicleOperatingState.Offline,
            [1] = TransportVehicleOperatingState.Idle,
            [2] = TransportVehicleOperatingState.Executing,
            [4] = TransportVehicleOperatingState.Faulted
        },
        CommandCodeMap = new Dictionary<TransportExecutionCommandType, int>
        {
            [TransportExecutionCommandType.MoveToNode] = 101,
            [TransportExecutionCommandType.Load] = 102,
            [TransportExecutionCommandType.Unload] = 103,
            [TransportExecutionCommandType.Stop] = 199
        }
    };

    private static ITransportDriverResolver CreateResolver(
        ITransportPlcSignalMapRegistry maps,
        ITransportDriverChannel channel) =>
        new TransportDriverResolver(new ITransportVehicleDriver[]
        {
            new SwitchableTransportVehicleDriver(TransportVehicleKind.Ems, maps, channel),
            new SwitchableTransportVehicleDriver(TransportVehicleKind.Rgv, maps, channel)
        });

    private sealed record DriverFixture(
        InMemoryTransportPlcSignalMapRegistry Registry,
        InMemoryTransportPlcAccessor Accessor,
        TransportDriverDiagnosticsService Diagnostics,
        TransportPlcDriverChannel Channel);
}
