namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

/// <summary>
/// 生产级故障接管：在调用任务重分配之前先检查已确认物理占用。
/// 原车辆未清场时不会创建或启动接替车辆，避免单轨区段出现双车风险。
/// </summary>
public sealed class SafeTransportFaultTakeoverService : ITransportFaultTakeoverService
{
    private readonly ITransportExecutionEngine _executions;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportTaskReassignmentService _reassignments;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ConcurrentDictionary<string, DateTime> _lastAttempts = new(StringComparer.Ordinal);
    private TransportFaultTakeoverReport _lastReport = new();

    public SafeTransportFaultTakeoverService(
        ITransportExecutionEngine executions,
        ITransportVehicleRegistry vehicles,
        ITransportTaskReassignmentService reassignments,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportTrafficCoordinator traffic,
        ITransportProductionTuningService tuning)
    {
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _vehicles = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
        _reassignments = reassignments ?? throw new ArgumentNullException(nameof(reassignments));
        _singleTrack = singleTrack ?? throw new ArgumentNullException(nameof(singleTrack));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
    }

    public async Task<TransportFaultTakeoverReport> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var items = new List<TransportFaultTakeoverItem>();
        foreach (var execution in _executions.GetAll().Where(x => !x.IsTerminal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _vehicles.TryGet(execution.VehicleId, out var vehicle);
            if (vehicle is { IsOnline: true } && vehicle.State != TransportVehicleOperatingState.Faulted)
                continue;

            if (_lastAttempts.TryGetValue(execution.RequestId, out var last) &&
                (now - last).TotalSeconds < _tuning.Current.FaultTakeoverCooldownSeconds)
            {
                items.Add(Item(execution, TransportFaultTakeoverDecision.Skipped, "仍处于故障接管冷却窗口"));
                continue;
            }
            _lastAttempts[execution.RequestId] = now;

            if (HasConfirmedOccupancy(execution.RequestId))
            {
                items.Add(Item(
                    execution,
                    TransportFaultTakeoverDecision.WaitingForPhysicalClearance,
                    "原车辆仍有已确认物理占用，禁止创建接替车辆，等待现场清场"));
                continue;
            }

            try
            {
                var result = await _reassignments.ReassignAsync(
                    execution.RequestId,
                    "第九阶段故障车辆自动接管",
                    true,
                    cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    items.Add(Item(
                        execution,
                        MapDecision(result.Record.Decision),
                        result.Record.Reason));
                    continue;
                }

                if (!_singleTrack.Release(execution.RequestId, requirePhysicalClearance: true))
                {
                    if (!string.IsNullOrWhiteSpace(result.Record.ReplacementRequestId))
                    {
                        _executions.Cancel(
                            result.Record.ReplacementRequestId,
                            "原车辆物理占用在接管期间发生变化，取消接替任务");
                    }
                    items.Add(new TransportFaultTakeoverItem
                    {
                        RequestId = execution.RequestId,
                        VehicleId = execution.VehicleId,
                        ReplacementVehicleId = result.Record.ReplacementVehicleId,
                        Decision = TransportFaultTakeoverDecision.WaitingForPhysicalClearance,
                        Message = "物理占用在接管期间发生变化，接替任务已停止，等待现场清场"
                    });
                    continue;
                }

                items.Add(new TransportFaultTakeoverItem
                {
                    RequestId = execution.RequestId,
                    VehicleId = execution.VehicleId,
                    ReplacementVehicleId = result.Record.ReplacementVehicleId,
                    Decision = TransportFaultTakeoverDecision.Reassigned,
                    Message = "故障任务已安全转移到接替车辆"
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(Item(execution, TransportFaultTakeoverDecision.Failed, ex.Message));
            }
        }

        var report = new TransportFaultTakeoverReport { Items = items };
        Volatile.Write(ref _lastReport, report);
        return report;
    }

    public TransportFaultTakeoverReport GetLastReport() => Volatile.Read(ref _lastReport);

    private bool HasConfirmedOccupancy(string requestId) =>
        _traffic.GetHolds().Any(x =>
            string.Equals(x.OwnerId, requestId, StringComparison.Ordinal) &&
            x.OccupancyConfirmed);

    private static TransportFaultTakeoverDecision MapDecision(
        TransportReassignmentDecision decision) => decision switch
    {
        TransportReassignmentDecision.ManualRecoveryRequired => TransportFaultTakeoverDecision.ManualRecoveryRequired,
        TransportReassignmentDecision.NoAlternativeVehicle => TransportFaultTakeoverDecision.NoAlternativeVehicle,
        TransportReassignmentDecision.SkippedTerminal => TransportFaultTakeoverDecision.Skipped,
        _ => TransportFaultTakeoverDecision.Failed
    };

    private static TransportFaultTakeoverItem Item(
        TransportExecutionSnapshot execution,
        TransportFaultTakeoverDecision decision,
        string message) => new()
    {
        RequestId = execution.RequestId,
        VehicleId = execution.VehicleId,
        Decision = decision,
        Message = message
    };
}
