using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportCommissioningGovernanceTests
{
    [Theory]
    [InlineData(TransportGovernedOperationType.WritePlcSignal, TransportPermissions.WritePlcSignal)]
    [InlineData(TransportGovernedOperationType.ResolveRecoveryConflict, TransportPermissions.ResolveRecoveryConflict)]
    [InlineData(TransportGovernedOperationType.RetryCommandCompensation, TransportPermissions.RetryCommandCompensation)]
    public async Task CommissioningDangerousOperation_RequiresIndependentApproval(
        TransportGovernedOperationType operationType,
        string permission)
    {
        var service = new TransportOperationGovernanceService(new InMemoryTransportGovernanceStore());
        var requester = Identity("requester", permission);
        var approver = Identity("approver", TransportPermissions.ApproveCriticalOperation);

        var requested = await service.RequestAsync(
            operationType,
            $"target:{operationType}",
            "现场联调操作",
            requester);
        var approved = await service.ApproveAsync(requested.Operation!.OperationId, approver, "现场已核对");
        var started = await service.BeginExecutionAsync(
            requested.Operation.OperationId,
            operationType,
            requested.Operation.TargetId,
            requester);

        Assert.True(requested.Success);
        Assert.Equal(TransportGovernedOperationState.PendingApproval, requested.Operation.State);
        Assert.True(approved.Success);
        Assert.Equal(TransportGovernedOperationState.Approved, approved.Operation!.State);
        Assert.True(started.Success);
        Assert.Equal(TransportGovernedOperationState.Executing, started.Operation!.State);
    }

    [Fact]
    public async Task CommissioningDangerousOperation_RequesterCannotApproveOwnRequest()
    {
        var permissions = new[]
        {
            TransportPermissions.WritePlcSignal,
            TransportPermissions.ApproveCriticalOperation
        };
        var actor = Identity("same-user", permissions);
        var service = new TransportOperationGovernanceService(new InMemoryTransportGovernanceStore());
        var requested = await service.RequestAsync(
            TransportGovernedOperationType.WritePlcSignal,
            "signal:EMS-01:DB100.Test",
            "单点写入联调",
            actor);

        var approved = await service.ApproveAsync(requested.Operation!.OperationId, actor);

        Assert.False(approved.Success);
        Assert.Contains("不同账号", approved.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandCompensation_RetryStopCompletesButMoveIsRejected()
    {
        var stateStore = new InMemoryTransportStateStore();
        await stateStore.SaveCommandAsync(new TransportCommandRecord
        {
            CommandId = "STOP-RETRY",
            RequestId = "REQ-STOP",
            VehicleId = "EMS-01",
            CommandType = TransportExecutionCommandType.Stop,
            Status = TransportCommandStatus.TimedOut
        });
        await stateStore.SaveCommandAsync(new TransportCommandRecord
        {
            CommandId = "MOVE-RETRY",
            RequestId = "REQ-MOVE",
            VehicleId = "EMS-01",
            CommandType = TransportExecutionCommandType.MoveToNode,
            TargetNodeId = "N2",
            Status = TransportCommandStatus.TimedOut
        });

        var diagnostics = new TransportDriverDiagnosticsService();
        diagnostics.Upsert(new TransportDriverDiagnosticSnapshot
        {
            VehicleId = "EMS-01",
            DriverId = "DRV-01",
            DeviceOnline = true
        });
        var maps = new InMemoryTransportPlcSignalMapRegistry();
        maps.Upsert(new TransportPlcSignalMap
        {
            VehicleId = "EMS-01",
            DriverId = "DRV-01",
            Kind = TransportVehicleKind.Ems,
            Mode = TransportDriverMode.Simulation,
            Enabled = true
        });
        var resolver = new TransportDriverResolver(new ITransportVehicleDriver[]
        {
            new SimulatorTransportVehicleDriver(TransportVehicleKind.Ems),
            new SimulatorTransportVehicleDriver(TransportVehicleKind.Rgv)
        });
        var service = new TransportCommandCompensationService(
            stateStore,
            diagnostics,
            maps,
            new TransportCommandDispatcher(resolver, stateStore),
            new InMemoryTransportCommunicationTraceStore());

        var stop = await service.RetrySafeStopAsync("STOP-RETRY");
        var moveError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RetrySafeStopAsync("MOVE-RETRY"));

        Assert.Equal(TransportCommandStatus.Completed, stop.Status);
        Assert.Contains("只有 Stop", moveError.Message, StringComparison.Ordinal);
    }

    private static TransportOperatorIdentity Identity(
        string userId,
        params string[] permissions) => new()
    {
        UserId = userId,
        DisplayName = userId,
        IsAuthenticated = true,
        Permissions = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase)
    };
}
