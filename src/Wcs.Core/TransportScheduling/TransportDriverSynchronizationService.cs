namespace Wcs.Core.TransportScheduling;

public interface ITransportDriverSynchronizationService
{
    Task<TransportDriverSyncReport> PollAllAsync(CancellationToken cancellationToken = default);
    Task<TransportDriverReconciliationReport> ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 将 PLC/控制器状态同步到车辆注册表和执行状态机。
/// 重启对账只生成差异报告，不自动恢复运动。
/// </summary>
public sealed class TransportDriverSynchronizationService : ITransportDriverSynchronizationService
{
    private readonly ITransportPlcSignalMapRegistry _maps;
    private readonly ITransportDriverResolver _drivers;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportStateStore _stateStore;
    private readonly ITransportExecutionEngine? _executions;

    public TransportDriverSynchronizationService(
        ITransportPlcSignalMapRegistry maps,
        ITransportDriverResolver drivers,
        ITransportVehicleRegistry vehicles,
        ITransportStateStore stateStore,
        ITransportExecutionEngine? executions = null)
    {
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _drivers = drivers ?? throw new ArgumentNullException(nameof(drivers));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _executions = executions;
    }

    public async Task<TransportDriverSyncReport> PollAllAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<TransportDriverSyncItem>();
        foreach (var map in _maps.GetAll().Where(x => x.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (map.Mode == TransportDriverMode.Simulation)
            {
                items.Add(Item(map.VehicleId, TransportDriverSyncDecision.SkippedSimulation, "车辆使用模拟驱动"));
                continue;
            }

            try
            {
                var state = await _drivers.Resolve(map.Kind)
                    .ReadStateAsync(map.VehicleId, cancellationToken)
                    .ConfigureAwait(false);

                _vehicles.TryGet(map.VehicleId, out var currentVehicle);
                if (HasVehicleStateChanged(currentVehicle, state))
                {
                    var snapshot = BuildVehicleSnapshot(map, state, currentVehicle);
                    _vehicles.Upsert(snapshot);
                    await _stateStore.SaveVehicleAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }

                await SynchronizeExecutionAsync(state, cancellationToken).ConfigureAwait(false);

                var decision = !state.IsOnline
                    ? TransportDriverSyncDecision.Offline
                    : state.FaultCode != 0 || state.OperatingState == TransportVehicleOperatingState.Faulted
                        ? TransportDriverSyncDecision.Faulted
                        : TransportDriverSyncDecision.Updated;
                var message = decision switch
                {
                    TransportDriverSyncDecision.Offline => "设备离线或心跳超时",
                    TransportDriverSyncDecision.Faulted => state.FaultMessage ?? $"设备故障码 {state.FaultCode}",
                    _ => "车辆状态已同步"
                };
                items.Add(Item(map.VehicleId, decision, message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(Item(map.VehicleId, TransportDriverSyncDecision.Failed, ex.Message));
            }
        }

        return new TransportDriverSyncReport { Items = items };
    }

    public async Task<TransportDriverReconciliationReport> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var persisted = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var persistedVehicles = persisted.Vehicles.ToDictionary(x => x.VehicleId, StringComparer.Ordinal);
        var activeCommands = persisted.Commands
            .Where(x => x.Status is TransportCommandStatus.Sent or TransportCommandStatus.Acknowledged)
            .GroupBy(x => x.VehicleId, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x.OrderByDescending(c => c.UpdatedAtUtc).First(),
                StringComparer.Ordinal);
        var items = new List<TransportDriverReconciliationItem>();

        foreach (var map in _maps.GetAll().Where(x => x.Enabled && x.Mode == TransportDriverMode.PlcTag))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var device = await _drivers.Resolve(map.Kind)
                    .ReadStateAsync(map.VehicleId, cancellationToken)
                    .ConfigureAwait(false);

                if (!persistedVehicles.TryGetValue(map.VehicleId, out var saved))
                {
                    items.Add(ReconcileItem(
                        map.VehicleId,
                        TransportDriverReconciliationDecision.VehicleNotPersisted,
                        string.Empty,
                        device.CurrentNodeId,
                        null,
                        device.ActiveCommandId,
                        "数据库中没有该车辆快照，必须人工确认初始位置"));
                    continue;
                }

                if (!device.IsOnline)
                {
                    items.Add(ReconcileItem(
                        map.VehicleId,
                        TransportDriverReconciliationDecision.DeviceOffline,
                        saved.CurrentNodeId,
                        device.CurrentNodeId,
                        null,
                        device.ActiveCommandId,
                        "设备离线，禁止自动恢复"));
                    continue;
                }

                if (!string.Equals(saved.CurrentNodeId, device.CurrentNodeId, StringComparison.Ordinal))
                {
                    items.Add(ReconcileItem(
                        map.VehicleId,
                        TransportDriverReconciliationDecision.PositionMismatch,
                        saved.CurrentNodeId,
                        device.CurrentNodeId,
                        activeCommands.GetValueOrDefault(map.VehicleId)?.CommandId,
                        device.ActiveCommandId,
                        "数据库位置与设备位置不一致，必须现场确认"));
                    continue;
                }

                var persistedCommand = activeCommands.GetValueOrDefault(map.VehicleId);
                if (persistedCommand is not null &&
                    !string.IsNullOrWhiteSpace(device.ActiveCommandId) &&
                    !string.Equals(persistedCommand.CommandId, device.ActiveCommandId, StringComparison.Ordinal))
                {
                    items.Add(ReconcileItem(
                        map.VehicleId,
                        TransportDriverReconciliationDecision.ActiveCommandMismatch,
                        saved.CurrentNodeId,
                        device.CurrentNodeId,
                        persistedCommand.CommandId,
                        device.ActiveCommandId,
                        "数据库活动命令与设备活动命令不一致"));
                    continue;
                }

                items.Add(ReconcileItem(
                    map.VehicleId,
                    TransportDriverReconciliationDecision.InSync,
                    saved.CurrentNodeId,
                    device.CurrentNodeId,
                    persistedCommand?.CommandId,
                    device.ActiveCommandId,
                    "车辆位置和活动命令一致，保持暂停等待上层确认"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(ReconcileItem(
                    map.VehicleId,
                    TransportDriverReconciliationDecision.Failed,
                    persistedVehicles.GetValueOrDefault(map.VehicleId)?.CurrentNodeId ?? string.Empty,
                    string.Empty,
                    activeCommands.GetValueOrDefault(map.VehicleId)?.CommandId,
                    null,
                    ex.Message));
            }
        }

        return new TransportDriverReconciliationReport { Items = items };
    }

    private static TransportVehicleSnapshot BuildVehicleSnapshot(
        TransportPlcSignalMap map,
        TransportDriverState state,
        TransportVehicleSnapshot? current) => new()
    {
        VehicleId = map.VehicleId,
        Kind = map.Kind,
        State = state.IsOnline ? state.OperatingState : TransportVehicleOperatingState.Offline,
        CurrentNodeId = string.IsNullOrWhiteSpace(state.CurrentNodeId)
            ? current?.CurrentNodeId ?? string.Empty
            : state.CurrentNodeId,
        IsOnline = state.IsOnline,
        BatteryPercent = Math.Clamp(state.BatteryPercent, 0, 100),
        Capabilities = current?.Capabilities ?? TransportVehicleCapability.All,
        ActiveTaskCount = current?.ActiveTaskCount ?? 0,
        Version = (current?.Version ?? 0) + 1,
        UpdatedAtUtc = state.UpdatedAtUtc
    };

    private static bool HasVehicleStateChanged(
        TransportVehicleSnapshot? current,
        TransportDriverState state)
    {
        if (current is null)
            return true;
        var targetState = state.IsOnline ? state.OperatingState : TransportVehicleOperatingState.Offline;
        var targetNode = string.IsNullOrWhiteSpace(state.CurrentNodeId) ? current.CurrentNodeId : state.CurrentNodeId;
        return current.IsOnline != state.IsOnline ||
               current.State != targetState ||
               !string.Equals(current.CurrentNodeId, targetNode, StringComparison.Ordinal) ||
               current.BatteryPercent != Math.Clamp(state.BatteryPercent, 0, 100);
    }

    private async Task SynchronizeExecutionAsync(
        TransportDriverState state,
        CancellationToken cancellationToken)
    {
        if (_executions is null)
            return;

        var active = _executions.GetAll()
            .Where(x => !x.IsTerminal)
            .Where(x => string.Equals(x.VehicleId, state.VehicleId, StringComparison.Ordinal))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefault();
        if (active is null)
            return;

        if ((state.FaultCode != 0 || state.OperatingState == TransportVehicleOperatingState.Faulted) &&
            active.State != TransportExecutionState.Faulted)
        {
            _executions.Fault(active.RequestId, state.FaultMessage ?? $"PLC 故障码 {state.FaultCode}");
        }
        else if (state.IsOnline &&
                 !string.IsNullOrWhiteSpace(state.CurrentNodeId) &&
                 state.Sequence > active.LastFeedbackSequence &&
                 !string.Equals(state.CurrentNodeId, active.CurrentNodeId, StringComparison.Ordinal))
        {
            _executions.ApplyPositionFeedback(new TransportPositionFeedback
            {
                VehicleId = state.VehicleId,
                NodeId = state.CurrentNodeId,
                Sequence = state.Sequence,
                OccurredAtUtc = state.UpdatedAtUtc
            });
        }

        if (_executions.TryGet(active.RequestId, out var updated) && updated is not null)
            await _stateStore.SaveExecutionAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private static TransportDriverSyncItem Item(
        string vehicleId,
        TransportDriverSyncDecision decision,
        string message) => new()
    {
        VehicleId = vehicleId,
        Decision = decision,
        Message = message
    };

    private static TransportDriverReconciliationItem ReconcileItem(
        string vehicleId,
        TransportDriverReconciliationDecision decision,
        string persistedNodeId,
        string deviceNodeId,
        string? persistedCommandId,
        string? deviceCommandId,
        string message) => new()
    {
        VehicleId = vehicleId,
        Decision = decision,
        PersistedNodeId = persistedNodeId,
        DeviceNodeId = deviceNodeId,
        PersistedCommandId = persistedCommandId,
        DeviceCommandId = deviceCommandId,
        Message = message
    };
}
