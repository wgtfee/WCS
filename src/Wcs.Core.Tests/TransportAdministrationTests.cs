using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportAdministrationTests
{
    [Fact]
    public async Task ConfigurationStore_UsesOptimisticVersion()
    {
        var store = new InMemoryTransportConfigurationStore();
        var first = await store.SaveAsync(new TransportRuntimeConfiguration
        {
            UpdatedBy = "operator-a"
        }, expectedVersion: 0);

        Assert.True(first.Success);
        Assert.Equal(1, first.Configuration!.Version);

        var conflict = await store.SaveAsync(new TransportRuntimeConfiguration
        {
            UpdatedBy = "operator-b"
        }, expectedVersion: 0);

        Assert.False(conflict.Success);
        Assert.True(conflict.VersionConflict);
        Assert.Equal(1, conflict.Configuration!.Version);
    }

    [Fact]
    public async Task ConfigurationService_AppliesPolicyResourcesStationsAndVehicles()
    {
        var graph = new TopologyGraph();
        graph.AddNode(new Node { NodeId = "N1" });
        graph.AddNode(new Node { NodeId = "C1" });
        graph.AddEdge(new Edge { EdgeId = "E1", FromNodeId = "N1", ToNodeId = "C1", Weight = 1 });
        var route = new TransportRouteCenter(graph);
        var vehicles = new InMemoryTransportVehicleRegistry();
        var traffic = new TransportTrafficCoordinator();
        var charging = new TransportChargingCoordinator(vehicles, route);
        var service = new TransportConfigurationService(
            new InMemoryTransportConfigurationStore(),
            traffic,
            charging,
            vehicles);

        var result = await service.SaveAndApplyAsync(new TransportRuntimeConfiguration
        {
            ChargingPolicy = new TransportChargingPolicy
            {
                ChargeThresholdPercent = 45,
                CriticalThresholdPercent = 10,
                MinimumDispatchBatteryPercent = 20,
                ResumeBatteryPercent = 80
            },
            TrafficResources = new[]
            {
                new TransportTrafficResourceDefinition
                {
                    ResourceId = "BLOCK-01",
                    EdgeIds = new[] { "E1" },
                    Enabled = true
                }
            },
            ChargingStations = new[]
            {
                new TransportChargingStationDefinition
                {
                    StationId = "CH-01",
                    NodeId = "C1",
                    Capacity = 1
                }
            },
            Vehicles = new[]
            {
                new TransportVehicleDefinition
                {
                    VehicleId = "EMS-01",
                    Kind = TransportVehicleKind.Ems,
                    InitialNodeId = "N1",
                    InitialBatteryPercent = 70
                }
            }
        }, expectedVersion: 0, updatedBy: "operator-a");

        Assert.True(result.Success);
        Assert.Equal(45, charging.Policy.ChargeThresholdPercent);
        Assert.Single(traffic.GetResources());
        Assert.Single(charging.GetStations());
        Assert.True(vehicles.TryGet("EMS-01", out var vehicle));
        Assert.Equal(TransportVehicleOperatingState.Offline, vehicle!.State);
    }

    [Fact]
    public async Task Governance_DangerousOperationRequiresDifferentApprover()
    {
        var store = new InMemoryTransportGovernanceStore();
        var service = new TransportOperationGovernanceService(store);
        var requester = Identity(
            "operator-a",
            TransportPermissions.ReassignTask,
            TransportPermissions.ApproveCriticalOperation);

        var requested = await service.RequestAsync(
            TransportGovernedOperationType.ReassignTask,
            "REQ-01",
            "车辆驱动故障",
            requester);

        Assert.True(requested.Success);
        Assert.Equal(TransportGovernedOperationState.PendingApproval, requested.Operation!.State);

        var selfApproval = await service.ApproveAsync(
            requested.Operation.OperationId,
            requester,
            "自己批准");

        Assert.False(selfApproval.Success);
        Assert.Contains("不同账号", selfApproval.Error);

        var approver = Identity("operator-b", TransportPermissions.ApproveCriticalOperation);
        var approved = await service.ApproveAsync(
            requested.Operation.OperationId,
            approver,
            "现场已确认原车停止");

        Assert.True(approved.Success);
        Assert.Equal(TransportGovernedOperationState.Approved, approved.Operation!.State);
        Assert.Single(approved.Operation.Approvals);
    }

    [Fact]
    public async Task Governance_ApprovedOperationCanOnlyBeginOnce()
    {
        var service = new TransportOperationGovernanceService(new InMemoryTransportGovernanceStore());
        var requester = Identity("operator-a", TransportPermissions.ForceReleaseTraffic);
        var approver = Identity("operator-b", TransportPermissions.ApproveCriticalOperation);

        var requested = await service.RequestAsync(
            TransportGovernedOperationType.ForceReleaseTraffic,
            "REQ-BLOCKED",
            "现场确认车辆已移出闭塞区",
            requester);
        await service.ApproveAsync(requested.Operation!.OperationId, approver);

        var first = await service.BeginExecutionAsync(
            requested.Operation.OperationId,
            TransportGovernedOperationType.ForceReleaseTraffic,
            "REQ-BLOCKED",
            requester);
        var second = await service.BeginExecutionAsync(
            requested.Operation.OperationId,
            TransportGovernedOperationType.ForceReleaseTraffic,
            "REQ-BLOCKED",
            requester);

        Assert.True(first.Success);
        Assert.Equal(TransportGovernedOperationState.Executing, first.Operation!.State);
        Assert.False(second.Success);
    }

    [Fact]
    public async Task Governance_RejectsExecutionTargetMismatch()
    {
        var service = new TransportOperationGovernanceService(new InMemoryTransportGovernanceStore());
        var requester = Identity("operator-a", TransportPermissions.ChangeConfiguration);
        var approver = Identity("operator-b", TransportPermissions.ApproveCriticalOperation);

        var requested = await service.RequestAsync(
            TransportGovernedOperationType.ChangeConfiguration,
            "runtime",
            "修改交通资源配置",
            requester);
        await service.ApproveAsync(requested.Operation!.OperationId, approver);

        var result = await service.BeginExecutionAsync(
            requested.Operation.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            "another-config",
            requester);

        Assert.False(result.Success);
        Assert.Contains("目标", result.Error);
    }

    [Fact]
    public async Task JournalStore_UpsertsSameBusinessRecord()
    {
        var store = new InMemoryTransportJournalStore();
        await store.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ChargingPlan,
            RecordId = "PLAN-01",
            PayloadJson = "{\"state\":\"Reserved\"}"
        });
        await store.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ChargingPlan,
            RecordId = "PLAN-01",
            PayloadJson = "{\"state\":\"Charging\"}"
        });

        var records = await store.QueryAsync(TransportJournalCategory.ChargingPlan);
        var record = Assert.Single(records);
        Assert.Contains("Charging", record.PayloadJson);
    }

    [Fact]
    public async Task ReliableDriver_ReusesExistingAcknowledgement()
    {
        var channel = new InMemoryTransportDriverChannel { AutoAcknowledge = false };
        channel.SetState(new TransportProtocolStateFrame
        {
            VehicleId = "EMS-01",
            DeviceOnline = true,
            OperatingState = TransportVehicleOperatingState.Idle,
            AcknowledgedCommandId = "CMD-01",
            AcknowledgedSequence = 8,
            CommandAccepted = true,
            CommandCompleted = true,
            HeartbeatAtUtc = DateTime.UtcNow
        });
        var driver = new ReliableTransportVehicleDriver(TransportVehicleKind.Ems, channel);

        var result = await driver.SendCommandAsync(new TransportExecutionCommand
        {
            CommandId = "CMD-01",
            RequestId = "REQ-01",
            VehicleId = "EMS-01",
            CommandType = TransportExecutionCommandType.Stop
        });

        Assert.True(result.Accepted);
        Assert.True(result.Completed);
        Assert.Null(channel.GetLastCommand("EMS-01"));
    }

    [Fact]
    public async Task ReliableDriver_StaleHeartbeatMarksVehicleOffline()
    {
        var channel = new InMemoryTransportDriverChannel();
        channel.SetState(new TransportProtocolStateFrame
        {
            VehicleId = "RGV-01",
            DeviceOnline = true,
            CurrentNodeId = "N1",
            OperatingState = TransportVehicleOperatingState.Idle,
            HeartbeatAtUtc = DateTime.UtcNow.AddSeconds(-20)
        });
        var driver = new ReliableTransportVehicleDriver(
            TransportVehicleKind.Rgv,
            channel,
            new ReliableTransportVehicleDriverOptions
            {
                HeartbeatTimeout = TimeSpan.FromSeconds(5),
                CommandAcknowledgementTimeout = TimeSpan.FromSeconds(1),
                PollInterval = TimeSpan.FromMilliseconds(10)
            });

        var state = await driver.ReadStateAsync("RGV-01");

        Assert.False(state.IsOnline);
        Assert.Equal(TransportVehicleOperatingState.Offline, state.OperatingState);
    }

    private static TransportOperatorIdentity Identity(string userId, params string[] permissions) => new()
    {
        UserId = userId,
        DisplayName = userId,
        IsAuthenticated = true,
        Permissions = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase)
    };
}
