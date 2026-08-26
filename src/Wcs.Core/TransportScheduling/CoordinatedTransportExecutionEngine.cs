namespace Wcs.Core.TransportScheduling;

/// <summary>
/// 第五阶段故障任务转移所需的原子执行控制。
/// </summary>
public interface ITransportReassignmentExecutionControl
{
    TransportExecutionResult FaultAndPrepareForReassignment(string requestId, string reason);
}

/// <summary>
/// 为现有内存执行引擎增加跨方法串行化边界。
/// 普通位置反馈、装载确认和故障换车准备都经过同一把内层门禁，
/// 从而避免“检查尚未取货后，车辆恰好进入装载状态”的竞态。
///
/// 性能说明：InMemoryTransportExecutionEngine 内部已对所有操作串行化，
/// 这里不再叠加外层锁；需要多步一致性的复合操作通过 ExecuteUnderGate
/// 在内层门禁下原子执行，避免嵌套双锁。
/// </summary>
public sealed class CoordinatedTransportExecutionEngine :
    ITransportExecutionEngine,
    ITransportReassignmentExecutionControl
{
    private readonly InMemoryTransportExecutionEngine _inner;
    private readonly ITransportVehicleRegistry _vehicles;

    public CoordinatedTransportExecutionEngine(
        InMemoryTransportExecutionEngine inner,
        ITransportVehicleRegistry vehicles)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
    }

    public TransportExecutionResult Create(string requestId) => _inner.Create(requestId);

    public TransportExecutionResult Start(string requestId) => _inner.Start(requestId);

    public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback) =>
        _inner.ApplyPositionFeedback(feedback);

    public TransportExecutionResult ConfirmLoaded(string requestId) => _inner.ConfirmLoaded(requestId);

    public TransportExecutionResult ConfirmUnloaded(string requestId) => _inner.ConfirmUnloaded(requestId);

    public TransportExecutionResult Pause(string requestId) => _inner.Pause(requestId);

    public TransportExecutionResult Resume(string requestId) => _inner.Resume(requestId);

    public TransportExecutionResult Fault(string requestId, string reason) => _inner.Fault(requestId, reason);

    public TransportExecutionResult Cancel(string requestId, string? reason = null) =>
        _inner.Cancel(requestId, reason);

    public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot) =>
        _inner.TryGet(requestId, out snapshot);

    /// <summary>按车辆查询当前活动执行任务（O(1) 索引查找）。</summary>
    public bool TryGetActiveByVehicle(string vehicleId, out TransportExecutionSnapshot? snapshot) =>
        _inner.TryGetActiveByVehicle(vehicleId, out snapshot);

    public IReadOnlyList<TransportExecutionSnapshot> GetAll() => _inner.GetAll();

    public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20) =>
        _inner.DequeueCommands(vehicleId, maxCount);

    public TransportExecutionResult FaultAndPrepareForReassignment(string requestId, string reason)
    {
        return _inner.ExecuteUnderGate(() =>
        {
            if (!_inner.TryGet(requestId, out var current) || current is null)
                return TransportExecutionResult.Failed("执行任务不存在");
            if (current.IsTerminal)
                return TransportExecutionResult.Failed("终态任务不能重新分配", current);

            if (!_vehicles.TryMarkFaulted(current.VehicleId))
            {
                return TransportExecutionResult.Failed(
                    "原车辆状态已变化，无法安全标记为故障",
                    current);
            }

            if (CanAutomaticallyReassign(current))
            {
                var cancelled = _inner.Cancel(
                    requestId,
                    string.IsNullOrWhiteSpace(reason) ? "故障车辆任务转移" : reason);

                if (cancelled.Success)
                    _vehicles.TryReleaseFaultedTask(current.VehicleId);

                return cancelled;
            }

            var faulted = _inner.Fault(
                requestId,
                string.IsNullOrWhiteSpace(reason) ? "车辆故障，等待现场恢复" : reason);

            return TransportExecutionResult.Failed(
                "任务已经到达取货点或载荷已绑定，原任务已停止，必须人工恢复",
                faulted.Snapshot ?? current);
        });
    }

    private static bool CanAutomaticallyReassign(TransportExecutionSnapshot execution) =>
        execution.State == TransportExecutionState.Assigned ||
        execution.State == TransportExecutionState.MovingToPickup ||
        (execution.State is (
                TransportExecutionState.WaitingForRoute or
                TransportExecutionState.Paused or
                TransportExecutionState.Faulted) &&
         execution.CurrentNodeIndex < execution.PickupNodeIndex);
}
