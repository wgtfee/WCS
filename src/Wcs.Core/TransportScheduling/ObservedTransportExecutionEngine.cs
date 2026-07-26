namespace Wcs.Core.TransportScheduling;

/// <summary>
/// 运输执行引擎的只读周期分析装饰器。所有业务判断和状态变更仍由
/// CoordinatedTransportExecutionEngine 完成；装饰器仅比较调用前后快照并送入周期模型。
/// </summary>
public sealed class ObservedTransportExecutionEngine :
    ITransportExecutionEngine,
    ITransportReassignmentExecutionControl
{
    private readonly CoordinatedTransportExecutionEngine _inner;
    private readonly ITransportCycleAnalysisService _analysis;
    private readonly bool _enabled;

    public ObservedTransportExecutionEngine(
        CoordinatedTransportExecutionEngine inner,
        ITransportCycleAnalysisService analysis,
        TransportCycleAnalysisOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _enabled = options?.Enabled ?? throw new ArgumentNullException(nameof(options));
    }

    public TransportExecutionResult Create(string requestId) =>
        Execute(requestId, nameof(Create), () => _inner.Create(requestId));

    public TransportExecutionResult Start(string requestId)
    {
        if (!_enabled) return _inner.Start(requestId);
        if (_inner.TryGet(requestId, out var existing) && existing is not null)
            return Execute(requestId, nameof(Start), () => _inner.Start(requestId));

        // InMemoryTransportExecutionEngine.Start 会内部调用 Create。为了保留 Assigned 阶段，
        // 装饰器在启用周期分析时显式拆成 Create + Start，但不改变原有业务结果。
        var created = _inner.Create(requestId);
        ObserveResult(null, created, nameof(Create));
        if (!created.Success || created.Snapshot is null) return created;

        var started = _inner.Start(requestId);
        ObserveResult(created.Snapshot, started, nameof(Start));
        return started;
    }

    public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (!_enabled) return _inner.ApplyPositionFeedback(feedback);
        var before = FindActiveByVehicle(feedback.VehicleId);
        var result = _inner.ApplyPositionFeedback(feedback);
        ObserveResult(before, result, nameof(ApplyPositionFeedback));
        return result;
    }

    public TransportExecutionResult ConfirmLoaded(string requestId) =>
        Execute(requestId, nameof(ConfirmLoaded), () => _inner.ConfirmLoaded(requestId));

    public TransportExecutionResult ConfirmUnloaded(string requestId) =>
        Execute(requestId, nameof(ConfirmUnloaded), () => _inner.ConfirmUnloaded(requestId));

    public TransportExecutionResult Pause(string requestId) =>
        Execute(requestId, nameof(Pause), () => _inner.Pause(requestId));

    public TransportExecutionResult Resume(string requestId) =>
        Execute(requestId, nameof(Resume), () => _inner.Resume(requestId));

    public TransportExecutionResult Fault(string requestId, string reason) =>
        Execute(requestId, nameof(Fault), () => _inner.Fault(requestId, reason));

    public TransportExecutionResult Cancel(string requestId, string? reason = null) =>
        Execute(requestId, nameof(Cancel), () => _inner.Cancel(requestId, reason));

    public TransportExecutionResult FaultAndPrepareForReassignment(string requestId, string reason) =>
        Execute(
            requestId,
            nameof(FaultAndPrepareForReassignment),
            () => _inner.FaultAndPrepareForReassignment(requestId, reason));

    public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot) =>
        _inner.TryGet(requestId, out snapshot);

    public IReadOnlyList<TransportExecutionSnapshot> GetAll() => _inner.GetAll();

    public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20) =>
        _inner.DequeueCommands(vehicleId, maxCount);

    private TransportExecutionResult Execute(
        string requestId,
        string operation,
        Func<TransportExecutionResult> action)
    {
        if (!_enabled) return action();
        _inner.TryGet(requestId, out var before);
        var result = action();
        ObserveResult(before, result, operation);
        return result;
    }

    private void ObserveResult(
        TransportExecutionSnapshot? before,
        TransportExecutionResult result,
        string operation)
    {
        var after = result.Snapshot;
        if (after is null) return;

        // 某些业务调用会以 Failed 返回，但已把任务切换到 WaitingForRoute 或 Faulted；
        // 只要快照真实变化，周期模型都必须观察到。
        if (before is not null && SnapshotsEquivalent(before, after)) return;
        _analysis.Observe(before, after, operation, result.Success);
    }

    private TransportExecutionSnapshot? FindActiveByVehicle(string vehicleId) =>
        _inner.GetAll()
            .Where(snapshot =>
                string.Equals(snapshot.VehicleId, vehicleId, StringComparison.Ordinal) &&
                snapshot.State is not (
                    TransportExecutionState.Completed or
                    TransportExecutionState.Cancelled or
                    TransportExecutionState.Faulted))
            .OrderByDescending(static snapshot => snapshot.UpdatedAtUtc)
            .FirstOrDefault();

    private static bool SnapshotsEquivalent(
        TransportExecutionSnapshot left,
        TransportExecutionSnapshot right) =>
        left.State == right.State &&
        left.CurrentNodeIndex == right.CurrentNodeIndex &&
        left.LastFeedbackSequence == right.LastFeedbackSequence &&
        string.Equals(left.LastError, right.LastError, StringComparison.Ordinal) &&
        left.UpdatedAtUtc == right.UpdatedAtUtc;
}
