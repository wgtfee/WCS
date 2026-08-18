namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public enum TransportReassignmentDecision
{
    Reassigned = 0,
    ManualRecoveryRequired = 1,
    NoAlternativeVehicle = 2,
    ExecutionNotFound = 3,
    AssignmentNotFound = 4,
    SkippedTerminal = 5,
    Failed = 6
}

public sealed record TransportTaskReassignmentRecord
{
    public string ReassignmentId { get; init; } = Guid.NewGuid().ToString("N");
    public string OriginalRequestId { get; init; } = string.Empty;
    public string? ReplacementRequestId { get; init; }
    public string OriginalVehicleId { get; init; } = string.Empty;
    public string? ReplacementVehicleId { get; init; }
    public TransportReassignmentDecision Decision { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportTaskReassignmentResult
{
    public bool Success { get; init; }
    public TransportTaskReassignmentRecord Record { get; init; } = new();
    public TransportDispatchAssignment? ReplacementAssignment { get; init; }
}

public interface ITransportTaskReassignmentService
{
    Task<TransportTaskReassignmentResult> ReassignAsync(
        string requestId,
        string reason,
        bool startImmediately = true,
        CancellationToken cancellationToken = default);

    IReadOnlyList<TransportTaskReassignmentRecord> GetHistory();
}

/// <summary>
/// 第五阶段故障车辆任务转移。
/// 仅允许尚未取货的任务自动换车；已装载、装载中或卸载中的任务必须进入现场恢复流程。
/// </summary>
public sealed class TransportTaskReassignmentService : ITransportTaskReassignmentService
{
    private readonly IUnifiedTransportDispatchEngine _dispatch;
    private readonly ITransportExecutionEngine _execution;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportReassignmentExecutionControl _executionControl;
    private readonly ConcurrentQueue<TransportTaskReassignmentRecord> _history = new();
    private long _sequence;

    public TransportTaskReassignmentService(
        IUnifiedTransportDispatchEngine dispatch,
        ITransportExecutionEngine execution,
        ITransportVehicleRegistry vehicles,
        ITransportReassignmentExecutionControl executionControl)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _executionControl = executionControl ?? throw new ArgumentNullException(nameof(executionControl));
    }

    public async Task<TransportTaskReassignmentResult> ReassignAsync(
        string requestId,
        string reason,
        bool startImmediately = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("RequestId 不能为空", nameof(requestId));

        if (!_execution.TryGet(requestId, out var execution) || execution is null)
        {
            return RecordFailure(
                requestId,
                string.Empty,
                TransportReassignmentDecision.ExecutionNotFound,
                "执行任务不存在");
        }

        if (execution.IsTerminal)
        {
            return RecordFailure(
                requestId,
                execution.VehicleId,
                TransportReassignmentDecision.SkippedTerminal,
                "终态任务不允许重新分配");
        }

        if (!_dispatch.TryGetAssignment(requestId, out var assignment) || assignment is null)
        {
            return RecordFailure(
                requestId,
                execution.VehicleId,
                TransportReassignmentDecision.AssignmentNotFound,
                "派单结果不存在");
        }

        var prepared = _executionControl.FaultAndPrepareForReassignment(
            requestId,
            string.IsNullOrWhiteSpace(reason) ? "故障车辆任务转移" : reason);

        if (!prepared.Success)
        {
            var decision = prepared.Snapshot is { IsTerminal: false }
                ? TransportReassignmentDecision.ManualRecoveryRequired
                : TransportReassignmentDecision.Failed;

            return RecordFailure(
                requestId,
                execution.VehicleId,
                decision,
                prepared.FailureReason ?? "原任务无法安全冻结");
        }

        var sourceNode = assignment.PickupNodePath.LastOrDefault();
        var destinationNode = assignment.LoadedNodePath.LastOrDefault();

        if (string.IsNullOrWhiteSpace(sourceNode) || string.IsNullOrWhiteSpace(destinationNode))
        {
            return RecordFailure(
                requestId,
                execution.VehicleId,
                TransportReassignmentDecision.Failed,
                "原任务路径信息不完整，无法生成接替任务");
        }

        var replacementRequestId =
            $"{requestId}:reassign:{Interlocked.Increment(ref _sequence):D3}";

        var dispatchResult = await _dispatch.DispatchAsync(new TransportDispatchRequest
        {
            RequestId = replacementRequestId,
            SourceNodeId = sourceNode,
            DestinationNodeId = destinationNode,
            LoadId = assignment.LoadId,
            Priority = assignment.Priority,
            RequiredCapability = assignment.RequiredCapability,
            RequiredEdgeCapability = assignment.RequiredEdgeCapability,
            AllowedVehicleKinds = new HashSet<TransportVehicleKind> { assignment.VehicleKind },
            RouteStrategy = assignment.RouteStrategy,
            ReservationLease = assignment.ReservationLease,
            ReservationWindowEdges = assignment.ReservationWindowEdges,
            MinimumBatteryPercent = assignment.MinimumBatteryPercent,
            AllowLowBatteryOverride = assignment.AllowLowBatteryOverride
        }, cancellationToken).ConfigureAwait(false);

        if (!dispatchResult.Success || dispatchResult.Assignment is null)
        {
            return RecordFailure(
                requestId,
                execution.VehicleId,
                TransportReassignmentDecision.NoAlternativeVehicle,
                dispatchResult.FailureReason ?? "没有可接替车辆",
                replacementRequestId);
        }

        var created = _execution.Create(replacementRequestId);
        if (!created.Success)
        {
            return RecordFailure(
                requestId,
                execution.VehicleId,
                TransportReassignmentDecision.Failed,
                created.FailureReason ?? "接替任务创建失败",
                replacementRequestId,
                dispatchResult.Assignment.VehicleId);
        }

        if (startImmediately)
        {
            var started = _execution.Start(replacementRequestId);
            if (!started.Success)
            {
                return RecordFailure(
                    requestId,
                    execution.VehicleId,
                    TransportReassignmentDecision.Failed,
                    started.FailureReason ?? "接替任务启动失败",
                    replacementRequestId,
                    dispatchResult.Assignment.VehicleId);
            }
        }

        var record = new TransportTaskReassignmentRecord
        {
            OriginalRequestId = requestId,
            ReplacementRequestId = replacementRequestId,
            OriginalVehicleId = execution.VehicleId,
            ReplacementVehicleId = dispatchResult.Assignment.VehicleId,
            Decision = TransportReassignmentDecision.Reassigned,
            Reason = string.IsNullOrWhiteSpace(reason) ? "故障车辆任务转移成功" : reason
        };
        _history.Enqueue(record);

        return new TransportTaskReassignmentResult
        {
            Success = true,
            Record = record,
            ReplacementAssignment = dispatchResult.Assignment
        };
    }

    public IReadOnlyList<TransportTaskReassignmentRecord> GetHistory() =>
        _history
            .OrderByDescending(x => x.OccurredAtUtc)
            .ToArray();

    private TransportTaskReassignmentResult RecordFailure(
        string requestId,
        string vehicleId,
        TransportReassignmentDecision decision,
        string reason,
        string? replacementRequestId = null,
        string? replacementVehicleId = null)
    {
        var record = new TransportTaskReassignmentRecord
        {
            OriginalRequestId = requestId,
            ReplacementRequestId = replacementRequestId,
            OriginalVehicleId = vehicleId,
            ReplacementVehicleId = replacementVehicleId,
            Decision = decision,
            Reason = reason
        };
        _history.Enqueue(record);
        return new TransportTaskReassignmentResult
        {
            Success = false,
            Record = record
        };
    }
}
