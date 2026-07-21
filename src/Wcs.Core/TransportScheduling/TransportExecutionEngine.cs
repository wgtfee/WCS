namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public interface ITransportExecutionEngine
{
    TransportExecutionResult Create(string requestId);
    TransportExecutionResult Start(string requestId);
    TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback);
    TransportExecutionResult ConfirmLoaded(string requestId);
    TransportExecutionResult ConfirmUnloaded(string requestId);
    TransportExecutionResult Pause(string requestId);
    TransportExecutionResult Resume(string requestId);
    TransportExecutionResult Fault(string requestId, string reason);
    TransportExecutionResult Cancel(string requestId, string? reason = null);
    bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot);
    IReadOnlyList<TransportExecutionSnapshot> GetAll();
    IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20);
}

/// <summary>
/// EMS/RGV 第二阶段执行引擎。
///
/// 职责：
/// - 管理运输任务执行状态机；
/// - 接收单调递增的位置反馈；
/// - 释放已通过路段并向前扩展滚动预留窗口；
/// - 生成与厂商协议无关的逻辑执行命令。
/// </summary>
public sealed class InMemoryTransportExecutionEngine : ITransportExecutionEngine
{
    private readonly IUnifiedTransportDispatchEngine _dispatchEngine;
    private readonly ITransportVehicleRegistry _vehicleRegistry;
    private readonly IRouteReservationManager _reservationManager;
    private readonly ConcurrentDictionary<string, TransportExecutionSnapshot> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TransportExecutionCommand>> _commands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InMemoryTransportExecutionEngine(
        IUnifiedTransportDispatchEngine dispatchEngine,
        ITransportVehicleRegistry vehicleRegistry,
        IRouteReservationManager reservationManager)
    {
        _dispatchEngine = dispatchEngine ?? throw new ArgumentNullException(nameof(dispatchEngine));
        _vehicleRegistry = vehicleRegistry ?? throw new ArgumentNullException(nameof(vehicleRegistry));
        _reservationManager = reservationManager ?? throw new ArgumentNullException(nameof(reservationManager));
    }

    public TransportExecutionResult Create(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return TransportExecutionResult.Failed("RequestId 不能为空");

        if (_executions.TryGetValue(requestId, out var existing))
            return TransportExecutionResult.Succeeded(existing);

        _gate.Wait();
        try
        {
            if (_executions.TryGetValue(requestId, out existing))
                return TransportExecutionResult.Succeeded(existing);

            if (!_dispatchEngine.TryGetAssignment(requestId, out var assignment) || assignment is null)
                return TransportExecutionResult.Failed("未找到派单结果，无法创建执行任务");

            if (!_vehicleRegistry.TryGet(assignment.VehicleId, out var vehicle) || vehicle is null)
                return TransportExecutionResult.Failed("派单车辆不存在");

            var fullNodes = assignment.FullNodePath;
            var fullEdges = assignment.FullEdgePath;
            if (fullNodes.Count == 0)
                return TransportExecutionResult.Failed("派单路径为空");

            var currentNodeIndex = FindNodeIndex(fullNodes, vehicle.CurrentNodeId, 0);
            if (currentNodeIndex < 0)
                currentNodeIndex = 0;

            _reservationManager.TryGet(assignment.ReservationId, out var reservation);

            var snapshot = new TransportExecutionSnapshot
            {
                RequestId = assignment.RequestId,
                AssignmentId = assignment.AssignmentId,
                VehicleId = assignment.VehicleId,
                LoadId = assignment.LoadId,
                State = TransportExecutionState.Assigned,
                CurrentNodeId = fullNodes[currentNodeIndex],
                CurrentNodeIndex = currentNodeIndex,
                PickupNodeIndex = Math.Max(0, assignment.PickupNodePath.Count - 1),
                FullNodePath = fullNodes,
                FullEdgePath = fullEdges,
                ActiveReservedEdges = reservation?.EdgeIds ?? Array.Empty<string>(),
                ReservationId = assignment.ReservationId,
                ReservationLease = assignment.ReservationLease,
                ReservationWindowEdges = assignment.ReservationWindowEdges
            };

            _executions[requestId] = snapshot;
            return TransportExecutionResult.Succeeded(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportExecutionResult Start(string requestId)
    {
        var created = Create(requestId);
        if (!created.Success || created.Snapshot is null)
            return created;

        _gate.Wait();
        try
        {
            var current = _executions[requestId];
            if (current.IsTerminal)
                return TransportExecutionResult.Failed("终态任务不能重新启动", current);
            if (current.State != TransportExecutionState.Assigned)
                return TransportExecutionResult.Succeeded(current);

            var nextState = ResolveMotionState(current);
            var next = current with
            {
                State = nextState,
                LastError = null,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _executions[requestId] = next;
            EnqueueNextCommand(next);
            return TransportExecutionResult.Succeeded(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (string.IsNullOrWhiteSpace(feedback.VehicleId))
            return TransportExecutionResult.Failed("VehicleId 不能为空");
        if (string.IsNullOrWhiteSpace(feedback.NodeId))
            return TransportExecutionResult.Failed("NodeId 不能为空");

        _gate.Wait();
        try
        {
            var current = _executions.Values
                .Where(x => string.Equals(x.VehicleId, feedback.VehicleId, StringComparison.Ordinal))
                .Where(x => !x.IsTerminal)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            if (current is null)
                return TransportExecutionResult.Failed("未找到该车辆的活动执行任务");

            if (feedback.Sequence <= current.LastFeedbackSequence)
                return TransportExecutionResult.Failed("位置反馈已重复或乱序", current);

            var nodeIndex = FindNodeIndex(current.FullNodePath, feedback.NodeId, current.CurrentNodeIndex);
            if (nodeIndex < 0)
                return TransportExecutionResult.Failed("反馈节点不在当前任务剩余路径中", current);

            if (_vehicleRegistry.TryGet(current.VehicleId, out var vehicle) && vehicle is not null)
            {
                _vehicleRegistry.Upsert(vehicle with
                {
                    CurrentNodeId = feedback.NodeId,
                    Version = vehicle.Version + 1,
                    UpdatedAtUtc = feedback.OccurredAtUtc
                });
            }

            var passedEdges = current.FullEdgePath
                .Take(nodeIndex)
                .ToArray();

            if (passedEdges.Length > 0)
                _reservationManager.ReleaseEdges(current.ReservationId, passedEdges);

            var desiredWindow = current.FullEdgePath
                .Skip(nodeIndex)
                .Take(current.ReservationWindowEdges)
                .ToArray();

            var extended = _reservationManager.TryExtend(
                current.ReservationId,
                desiredWindow,
                current.ReservationLease,
                out var reservation);

            var state = extended
                ? ResolveMotionState(current with { CurrentNodeIndex = nodeIndex })
                : TransportExecutionState.WaitingForRoute;

            var next = current with
            {
                CurrentNodeId = feedback.NodeId,
                CurrentNodeIndex = nodeIndex,
                LastFeedbackSequence = feedback.Sequence,
                State = state,
                ActiveReservedEdges = reservation?.EdgeIds ?? Array.Empty<string>(),
                LastError = extended ? null : "前方闭塞路段暂不可预留",
                UpdatedAtUtc = feedback.OccurredAtUtc
            };

            _executions[current.RequestId] = next;

            if (extended)
                EnqueueNextCommand(next);

            return extended
                ? TransportExecutionResult.Succeeded(next)
                : TransportExecutionResult.Failed(next.LastError!, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportExecutionResult ConfirmLoaded(string requestId) =>
        TransitionWithCommand(
            requestId,
            TransportExecutionState.Loading,
            TransportExecutionState.MovingToDestination,
            "任务不在装载确认状态");

    public TransportExecutionResult ConfirmUnloaded(string requestId)
    {
        _gate.Wait();
        try
        {
            if (!_executions.TryGetValue(requestId, out var current))
                return TransportExecutionResult.Failed("执行任务不存在");
            if (current.State != TransportExecutionState.Unloading)
                return TransportExecutionResult.Failed("任务不在卸载确认状态", current);

            _dispatchEngine.Complete(requestId);

            var next = current with
            {
                State = TransportExecutionState.Completed,
                ActiveReservedEdges = Array.Empty<string>(),
                LastError = null,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _executions[requestId] = next;
            return TransportExecutionResult.Succeeded(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportExecutionResult Pause(string requestId)
    {
        _gate.Wait();
        try
        {
            if (!_executions.TryGetValue(requestId, out var current))
                return TransportExecutionResult.Failed("执行任务不存在");
            if (current.IsTerminal)
                return TransportExecutionResult.Failed("终态任务不能暂停", current);

            var next = current with
            {
                State = TransportExecutionState.Paused,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _executions[requestId] = next;
            EnqueueCommand(next, TransportExecutionCommandType.Stop);
            return TransportExecutionResult.Succeeded(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportExecutionResult Resume(string requestId)
    {
        _gate.Wait();
        try
        {
            if (!_executions.TryGetValue(requestId, out var current))
                return TransportExecutionResult.Failed("执行任务不存在");
            if (current.State is not (TransportExecutionState.Paused or TransportExecutionState.WaitingForRoute))
                return TransportExecutionResult.Failed("任务不在可恢复状态", current);

            var desiredWindow = current.FullEdgePath
                .Skip(current.CurrentNodeIndex)
                .Take(current.ReservationWindowEdges)
                .ToArray();

            if (!_reservationManager.TryExtend(
                    current.ReservationId,
                    desiredWindow,
                    current.ReservationLease,
                    out var reservation))
            {
                var waiting = current with
                {
                    State = TransportExecutionState.WaitingForRoute,
                    ActiveReservedEdges = reservation?.EdgeIds ?? current.ActiveReservedEdges,
                    LastError = "恢复失败：前方闭塞路段仍被占用",
                    UpdatedAtUtc = DateTime.UtcNow
                };
                _executions[requestId] = waiting;
                return TransportExecutionResult.Failed(waiting.LastError!, waiting);
            }

            var next = current with
            {
                State = ResolveMotionState(current),
                ActiveReservedEdges = reservation?.EdgeIds ?? Array.Empty<string>(),
                LastError = null,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _executions[requestId] = next;
            EnqueueNextCommand(next);
            return TransportExecutionResult.Succeeded(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportExecutionResult Fault(string requestId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            reason = "车辆或执行层故障";

        _gate.Wait();
        try
        {
            if (!_executions.TryGetValue(requestId, out var current))
                return TransportExecutionResult.Failed("执行任务不存在");
            if (current.IsTerminal)
                return TransportExecutionResult.Failed("终态任务不能转为故障", current);

            var next = current with
            {
                State = TransportExecutionState.Faulted,
                LastError = reason,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _executions[requestId] = next;
            EnqueueCommand(next, TransportExecutionCommandType.Stop);
            return TransportExecutionResult.Succeeded(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public TransportExecutionResult Cancel(string requestId, string? reason = null)
    {
        _gate.Wait();
        try
        {
            if (!_executions.TryGetValue(requestId, out var current))
                return TransportExecutionResult.Failed("执行任务不存在");
            if (current.IsTerminal)
                return TransportExecutionResult.Succeeded(current);

            EnqueueCommand(current, TransportExecutionCommandType.Stop);
            _dispatchEngine.Complete(requestId);

            var next = current with
            {
                State = TransportExecutionState.Cancelled,
                ActiveReservedEdges = Array.Empty<string>(),
                LastError = reason,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _executions[requestId] = next;
            return TransportExecutionResult.Succeeded(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            snapshot = null;
            return false;
        }

        return _executions.TryGetValue(requestId, out snapshot);
    }

    public IReadOnlyList<TransportExecutionSnapshot> GetAll() =>
        _executions.Values
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToList();

    public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20)
    {
        if (string.IsNullOrWhiteSpace(vehicleId) || maxCount <= 0)
            return Array.Empty<TransportExecutionCommand>();

        if (!_commands.TryGetValue(vehicleId, out var queue))
            return Array.Empty<TransportExecutionCommand>();

        var result = new List<TransportExecutionCommand>(maxCount);
        while (result.Count < maxCount && queue.TryDequeue(out var command))
            result.Add(command);

        return result;
    }

    private TransportExecutionResult TransitionWithCommand(
        string requestId,
        TransportExecutionState expected,
        TransportExecutionState target,
        string failureReason)
    {
        _gate.Wait();
        try
        {
            if (!_executions.TryGetValue(requestId, out var current))
                return TransportExecutionResult.Failed("执行任务不存在");
            if (current.State != expected)
                return TransportExecutionResult.Failed(failureReason, current);

            var next = current with
            {
                State = target,
                LastError = null,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _executions[requestId] = next;
            EnqueueNextCommand(next);
            return TransportExecutionResult.Succeeded(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TransportExecutionState ResolveMotionState(TransportExecutionSnapshot snapshot)
    {
        if (snapshot.CurrentNodeIndex >= snapshot.FullNodePath.Count - 1)
            return TransportExecutionState.Unloading;
        if (snapshot.CurrentNodeIndex == snapshot.PickupNodeIndex)
            return TransportExecutionState.Loading;
        if (snapshot.CurrentNodeIndex < snapshot.PickupNodeIndex)
            return TransportExecutionState.MovingToPickup;
        return TransportExecutionState.MovingToDestination;
    }

    private void EnqueueNextCommand(TransportExecutionSnapshot snapshot)
    {
        switch (snapshot.State)
        {
            case TransportExecutionState.MovingToPickup:
            case TransportExecutionState.MovingToDestination:
                var nextIndex = snapshot.CurrentNodeIndex + 1;
                if (nextIndex < snapshot.FullNodePath.Count)
                    EnqueueCommand(snapshot, TransportExecutionCommandType.MoveToNode, snapshot.FullNodePath[nextIndex]);
                break;

            case TransportExecutionState.Loading:
                EnqueueCommand(snapshot, TransportExecutionCommandType.Load);
                break;

            case TransportExecutionState.Unloading:
                EnqueueCommand(snapshot, TransportExecutionCommandType.Unload);
                break;
        }
    }

    private void EnqueueCommand(
        TransportExecutionSnapshot snapshot,
        TransportExecutionCommandType type,
        string? targetNodeId = null)
    {
        var queue = _commands.GetOrAdd(snapshot.VehicleId, _ => new ConcurrentQueue<TransportExecutionCommand>());
        queue.Enqueue(new TransportExecutionCommand
        {
            RequestId = snapshot.RequestId,
            VehicleId = snapshot.VehicleId,
            CommandType = type,
            TargetNodeId = targetNodeId
        });
    }

    private static int FindNodeIndex(IReadOnlyList<string> nodes, string nodeId, int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i], nodeId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
