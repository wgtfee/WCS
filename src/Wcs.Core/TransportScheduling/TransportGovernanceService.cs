namespace Wcs.Core.TransportScheduling;

public sealed record TransportGovernanceResult
{
    public bool Success { get; init; }
    public TransportGovernedOperation? Operation { get; init; }
    public string? Error { get; init; }

    public static TransportGovernanceResult Ok(TransportGovernedOperation operation) =>
        new() { Success = true, Operation = operation };

    public static TransportGovernanceResult Fail(string error, TransportGovernedOperation? operation = null) =>
        new() { Error = error, Operation = operation };
}

public interface ITransportOperationGovernanceService
{
    Task<TransportGovernanceResult> RequestAsync(
        TransportGovernedOperationType operationType,
        string targetId,
        string reason,
        TransportOperatorIdentity requester,
        TimeSpan? validity = null,
        CancellationToken cancellationToken = default);

    Task<TransportGovernanceResult> ApproveAsync(
        string operationId,
        TransportOperatorIdentity approver,
        string? comment = null,
        CancellationToken cancellationToken = default);

    Task<TransportGovernanceResult> RejectAsync(
        string operationId,
        TransportOperatorIdentity approver,
        string reason,
        CancellationToken cancellationToken = default);

    Task<TransportGovernanceResult> BeginExecutionAsync(
        string operationId,
        TransportGovernedOperationType expectedType,
        string expectedTargetId,
        TransportOperatorIdentity executor,
        CancellationToken cancellationToken = default);

    Task CompleteExecutionAsync(
        string operationId,
        TransportOperatorIdentity executor,
        bool success,
        string resultMessage,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransportGovernedOperation>> GetOperationsAsync(int maxCount = 200, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportAuditRecord>> GetAuditsAsync(int maxCount = 500, CancellationToken cancellationToken = default);
}

public sealed class TransportOperationGovernanceService : ITransportOperationGovernanceService
{
    private readonly ITransportGovernanceStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TransportOperationGovernanceService(ITransportGovernanceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<TransportGovernanceResult> RequestAsync(
        TransportGovernedOperationType operationType,
        string targetId,
        string reason,
        TransportOperatorIdentity requester,
        TimeSpan? validity = null,
        CancellationToken cancellationToken = default)
    {
        var permission = RequiredPermission(operationType);
        if (!requester.HasPermission(permission))
            return TransportGovernanceResult.Fail($"缺少权限：{permission}");
        if (string.IsNullOrWhiteSpace(requester.UserId))
            return TransportGovernanceResult.Fail("认证身份缺少稳定 UserId");
        if (validity.HasValue && (validity.Value <= TimeSpan.Zero || validity.Value > TimeSpan.FromHours(24)))
            return TransportGovernanceResult.Fail("审批有效期必须大于 0 且不超过 24 小时");
        if (string.IsNullOrWhiteSpace(targetId))
            return TransportGovernanceResult.Fail("TargetId 不能为空");
        if (string.IsNullOrWhiteSpace(reason))
            return TransportGovernanceResult.Fail("危险操作必须填写原因");

        var approvalCount = RequiresIndependentApproval(operationType) ? 1 : 0;
        var operation = new TransportGovernedOperation
        {
            OperationType = operationType,
            TargetId = targetId,
            Reason = reason.Trim(),
            RequestedBy = requester.UserId,
            RequestedByName = requester.DisplayName,
            RequiredPermission = permission,
            RequiredIndependentApprovals = approvalCount,
            State = approvalCount == 0
                ? TransportGovernedOperationState.Approved
                : TransportGovernedOperationState.PendingApproval,
            ExpiresAtUtc = DateTime.UtcNow.Add(validity ?? TimeSpan.FromMinutes(15))
        };

        await _store.SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        await AuditAsync(operation, "Requested", requester, true, reason, cancellationToken).ConfigureAwait(false);
        return TransportGovernanceResult.Ok(operation);
    }

    public async Task<TransportGovernanceResult> ApproveAsync(
        string operationId,
        TransportOperatorIdentity approver,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        if (!approver.HasPermission(TransportPermissions.ApproveCriticalOperation))
            return TransportGovernanceResult.Fail($"缺少权限：{TransportPermissions.ApproveCriticalOperation}");
        if (string.IsNullOrWhiteSpace(approver.UserId))
            return TransportGovernanceResult.Fail("认证身份缺少稳定 UserId");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _store.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return TransportGovernanceResult.Fail("审批操作不存在");
            var expired = await ExpireIfNeededAsync(current, approver, cancellationToken).ConfigureAwait(false);
            if (expired is not null)
                return expired;
            if (current.State != TransportGovernedOperationState.PendingApproval)
                return TransportGovernanceResult.Fail("当前状态不允许审批", current);
            if (string.Equals(current.RequestedBy, approver.UserId, StringComparison.Ordinal))
                return TransportGovernanceResult.Fail("申请人与审批人必须是不同账号", current);
            if (current.Approvals.Any(x => string.Equals(x.UserId, approver.UserId, StringComparison.Ordinal)))
                return TransportGovernanceResult.Fail("同一审批人不能重复审批", current);

            var approvals = current.Approvals.Append(new TransportOperationApproval
            {
                UserId = approver.UserId,
                DisplayName = approver.DisplayName,
                Comment = comment
            }).ToArray();

            var next = current with
            {
                Approvals = approvals,
                State = approvals.Count >= current.RequiredIndependentApprovals
                    ? TransportGovernedOperationState.Approved
                    : TransportGovernedOperationState.PendingApproval,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _store.SaveOperationAsync(next, cancellationToken).ConfigureAwait(false);
            await AuditAsync(next, "Approved", approver, true, comment, cancellationToken).ConfigureAwait(false);
            return TransportGovernanceResult.Ok(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransportGovernanceResult> RejectAsync(
        string operationId,
        TransportOperatorIdentity approver,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!approver.HasPermission(TransportPermissions.ApproveCriticalOperation))
            return TransportGovernanceResult.Fail($"缺少权限：{TransportPermissions.ApproveCriticalOperation}");
        if (string.IsNullOrWhiteSpace(approver.UserId))
            return TransportGovernanceResult.Fail("认证身份缺少稳定 UserId");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _store.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return TransportGovernanceResult.Fail("审批操作不存在");
            if (current.State is not (TransportGovernedOperationState.PendingApproval or TransportGovernedOperationState.Approved))
                return TransportGovernanceResult.Fail("当前状态不允许拒绝", current);

            var next = current with
            {
                State = TransportGovernedOperationState.Rejected,
                ResultMessage = reason,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _store.SaveOperationAsync(next, cancellationToken).ConfigureAwait(false);
            await AuditAsync(next, "Rejected", approver, true, reason, cancellationToken).ConfigureAwait(false);
            return TransportGovernanceResult.Ok(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransportGovernanceResult> BeginExecutionAsync(
        string operationId,
        TransportGovernedOperationType expectedType,
        string expectedTargetId,
        TransportOperatorIdentity executor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executor.UserId))
            return TransportGovernanceResult.Fail("认证身份缺少稳定 UserId");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _store.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return TransportGovernanceResult.Fail("审批操作不存在");
            var expired = await ExpireIfNeededAsync(current, executor, cancellationToken).ConfigureAwait(false);
            if (expired is not null)
                return expired;
            if (current.State != TransportGovernedOperationState.Approved)
                return TransportGovernanceResult.Fail("操作尚未完成审批或已经执行", current);
            if (current.OperationType != expectedType ||
                !string.Equals(current.TargetId, expectedTargetId, StringComparison.Ordinal))
            {
                return TransportGovernanceResult.Fail("审批目标与实际执行目标不一致", current);
            }
            if (!executor.HasPermission(current.RequiredPermission))
                return TransportGovernanceResult.Fail($"执行人缺少权限：{current.RequiredPermission}", current);

            var next = current with
            {
                State = TransportGovernedOperationState.Executing,
                ExecutionNonce = Guid.NewGuid().ToString("N"),
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _store.SaveOperationAsync(next, cancellationToken).ConfigureAwait(false);
            await AuditAsync(next, "ExecutionStarted", executor, true, null, cancellationToken).ConfigureAwait(false);
            return TransportGovernanceResult.Ok(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteExecutionAsync(
        string operationId,
        TransportOperatorIdentity executor,
        bool success,
        string resultMessage,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _store.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.State != TransportGovernedOperationState.Executing)
                return;

            var next = current with
            {
                State = success ? TransportGovernedOperationState.Executed : TransportGovernedOperationState.Failed,
                ResultMessage = resultMessage,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _store.SaveOperationAsync(next, cancellationToken).ConfigureAwait(false);
            await AuditAsync(next, success ? "ExecutionCompleted" : "ExecutionFailed", executor, success, resultMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<TransportGovernedOperation>> GetOperationsAsync(int maxCount = 200, CancellationToken cancellationToken = default) =>
        _store.GetOperationsAsync(maxCount, cancellationToken);

    public Task<IReadOnlyList<TransportAuditRecord>> GetAuditsAsync(int maxCount = 500, CancellationToken cancellationToken = default) =>
        _store.GetAuditsAsync(maxCount, cancellationToken);

    private async Task<TransportGovernanceResult?> ExpireIfNeededAsync(
        TransportGovernedOperation current,
        TransportOperatorIdentity actor,
        CancellationToken cancellationToken)
    {
        if (current.ExpiresAtUtc > DateTime.UtcNow)
            return null;

        var expired = current with
        {
            State = TransportGovernedOperationState.Expired,
            ResultMessage = "审批已过期",
            UpdatedAtUtc = DateTime.UtcNow
        };
        await _store.SaveOperationAsync(expired, cancellationToken).ConfigureAwait(false);
        await AuditAsync(expired, "Expired", actor, false, expired.ResultMessage, cancellationToken).ConfigureAwait(false);
        return TransportGovernanceResult.Fail("审批已过期", expired);
    }

    private Task AuditAsync(
        TransportGovernedOperation operation,
        string action,
        TransportOperatorIdentity actor,
        bool success,
        string? detail,
        CancellationToken cancellationToken) =>
        _store.AppendAuditAsync(new TransportAuditRecord
        {
            OperationId = operation.OperationId,
            Action = action,
            ActorId = actor.UserId,
            ActorName = actor.DisplayName,
            TargetId = operation.TargetId,
            Detail = detail,
            Success = success
        }, cancellationToken);

    private static string RequiredPermission(TransportGovernedOperationType operationType) => operationType switch
    {
        TransportGovernedOperationType.ChangeConfiguration => TransportPermissions.ChangeConfiguration,
        TransportGovernedOperationType.ReassignTask => TransportPermissions.ReassignTask,
        TransportGovernedOperationType.ForceReleaseTraffic => TransportPermissions.ForceReleaseTraffic,
        TransportGovernedOperationType.OverrideLowBattery => TransportPermissions.OverrideLowBattery,
        TransportGovernedOperationType.SendManualDriverCommand => TransportPermissions.SendManualDriverCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(operationType))
    };

    private static bool RequiresIndependentApproval(TransportGovernedOperationType operationType) => operationType is
        TransportGovernedOperationType.ChangeConfiguration or
        TransportGovernedOperationType.ReassignTask or
        TransportGovernedOperationType.ForceReleaseTraffic or
        TransportGovernedOperationType.SendManualDriverCommand;
}
