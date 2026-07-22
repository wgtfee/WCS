namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public static class TransportPermissions
{
    public const string ReadAdministration = "transport.admin.read";
    public const string ChangeConfiguration = "transport.config.change";
    public const string ReassignTask = "transport.task.reassign";
    public const string ForceReleaseTraffic = "transport.traffic.force-release";
    public const string OverrideLowBattery = "transport.battery.override";
    public const string SendManualDriverCommand = "transport.driver.manual-command";
    public const string WritePlcSignal = "transport.driver.signal-write";
    public const string ResolveRecoveryConflict = "transport.recovery.resolve";
    public const string RetryCommandCompensation = "transport.command.compensate";
    public const string ApproveCriticalOperation = "transport.operation.approve";
}

public enum TransportGovernedOperationType
{
    ChangeConfiguration = 0,
    ReassignTask = 1,
    ForceReleaseTraffic = 2,
    OverrideLowBattery = 3,
    SendManualDriverCommand = 4,
    WritePlcSignal = 5,
    ResolveRecoveryConflict = 6,
    RetryCommandCompensation = 7
}

public enum TransportGovernedOperationState
{
    PendingApproval = 0,
    Approved = 1,
    Executing = 2,
    Executed = 3,
    Rejected = 4,
    Failed = 5,
    Expired = 6
}

public sealed record TransportOperatorIdentity
{
    public string UserId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsAuthenticated { get; init; }
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool HasPermission(string permission) =>
        IsAuthenticated && Permissions.Contains(permission);
}

public sealed record TransportOperationApproval
{
    public string UserId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Comment { get; init; }
    public DateTime ApprovedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportGovernedOperation
{
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");
    public TransportGovernedOperationType OperationType { get; init; }
    public string TargetId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string RequestedBy { get; init; } = string.Empty;
    public string RequestedByName { get; init; } = string.Empty;
    public string RequiredPermission { get; init; } = string.Empty;
    public int RequiredIndependentApprovals { get; init; }
    public IReadOnlyList<TransportOperationApproval> Approvals { get; init; } = Array.Empty<TransportOperationApproval>();
    public TransportGovernedOperationState State { get; init; } = TransportGovernedOperationState.PendingApproval;
    public string? ExecutionNonce { get; init; }
    public string? ResultMessage { get; init; }
    public DateTime RequestedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; init; } = DateTime.UtcNow.AddMinutes(15);
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportAuditRecord
{
    public string AuditId { get; init; } = Guid.NewGuid().ToString("N");
    public string OperationId { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string ActorId { get; init; } = string.Empty;
    public string ActorName { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public bool Success { get; init; }
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ITransportGovernanceStore
{
    Task SaveOperationAsync(TransportGovernedOperation operation, CancellationToken cancellationToken = default);
    Task<TransportGovernedOperation?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportGovernedOperation>> GetOperationsAsync(int maxCount = 200, CancellationToken cancellationToken = default);
    Task AppendAuditAsync(TransportAuditRecord audit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TransportAuditRecord>> GetAuditsAsync(int maxCount = 500, CancellationToken cancellationToken = default);
}
