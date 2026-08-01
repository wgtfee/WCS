namespace Wcs.Simulator.VirtualExternal;

public sealed class VirtualExternalOptions
{
    public const string SectionName = "SimulationVirtualExternal";

    public int MaximumEndpoints { get; set; } = 256;
    public int MaximumFaults { get; set; } = 2_048;
    public int MaximumRequests { get; set; } = 10_000;
    public int MaximumAuditRecords { get; set; } = 5_000;
    public int MaximumRetryAttempts { get; set; } = 16;
    public long DefaultTimeoutMilliseconds { get; set; } = 5_000;
    public long MaximumDelayMilliseconds { get; set; } = 86_400_000;
    public int CircuitFailureThreshold { get; set; } = 3;
    public long CircuitOpenMilliseconds { get; set; } = 30_000;

    public void Validate()
    {
        if (MaximumEndpoints is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualExternal.MaximumEndpoints must be between 1 and 100,000.");
        if (MaximumFaults is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualExternal.MaximumFaults must be between 1 and 1,000,000.");
        if (MaximumRequests is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualExternal.MaximumRequests must be between 1 and 1,000,000.");
        if (MaximumAuditRecords is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationVirtualExternal.MaximumAuditRecords must be between 1 and 1,000,000.");
        if (MaximumRetryAttempts is < 1 or > 100)
            throw new InvalidOperationException("SimulationVirtualExternal.MaximumRetryAttempts must be between 1 and 100.");
        if (DefaultTimeoutMilliseconds is < 1 || DefaultTimeoutMilliseconds > MaximumDelayMilliseconds)
            throw new InvalidOperationException("SimulationVirtualExternal.DefaultTimeoutMilliseconds is outside the configured delay limit.");
        if (MaximumDelayMilliseconds is < 1 or > 31_536_000_000)
            throw new InvalidOperationException("SimulationVirtualExternal.MaximumDelayMilliseconds must be between 1 millisecond and 365 days.");
        if (CircuitFailureThreshold is < 1 or > 100)
            throw new InvalidOperationException("SimulationVirtualExternal.CircuitFailureThreshold must be between 1 and 100.");
        if (CircuitOpenMilliseconds is < 1 || CircuitOpenMilliseconds > MaximumDelayMilliseconds)
            throw new InvalidOperationException("SimulationVirtualExternal.CircuitOpenMilliseconds is outside the configured delay limit.");
    }
}

public enum VirtualExternalSystemKind
{
    Mes,
    SqlServer,
    Http,
    Network,
    Custom
}

public enum VirtualExternalFaultKind
{
    Timeout,
    Unavailable,
    HttpStatus,
    InvalidResponse,
    DuplicateResponse,
    SqlDeadlock,
    SqlCommandTimeout,
    ConnectionReset,
    HighLatency,
    PacketLoss,
    HalfOpen
}

public enum VirtualExternalRequestState
{
    Succeeded,
    Failed,
    TimedOut,
    RejectedByCircuit
}

public enum VirtualExternalCircuitState
{
    Closed,
    Open,
    HalfOpen
}

public sealed record VirtualExternalEndpointDefinition(
    string EndpointId,
    VirtualExternalSystemKind Kind);

public sealed record VirtualExternalEndpointSnapshot(
    string EndpointId,
    VirtualExternalSystemKind Kind,
    VirtualExternalCircuitState CircuitState,
    int ConsecutiveFailures,
    long? CircuitOpenUntilOffsetMilliseconds,
    long Version);

public sealed record VirtualExternalFaultDefinition(
    string FaultId,
    string EndpointId,
    VirtualExternalFaultKind Kind,
    long StartsAtOffsetMilliseconds,
    long EndsAtOffsetMilliseconds,
    int? HttpStatusCode = null,
    long DelayMilliseconds = 0,
    string? ErrorCode = null);

public sealed record VirtualExternalFaultSnapshot(
    string FaultId,
    string EndpointId,
    VirtualExternalFaultKind Kind,
    long StartsAtOffsetMilliseconds,
    long EndsAtOffsetMilliseconds,
    int? HttpStatusCode,
    long DelayMilliseconds,
    string? ErrorCode,
    bool Cleared,
    long Version);

public sealed record VirtualExternalAttemptSnapshot(
    int Attempt,
    long VirtualOffsetMilliseconds,
    VirtualExternalRequestState State,
    long DurationMilliseconds,
    int? HttpStatusCode,
    string? ErrorCode,
    bool DuplicateResponse,
    string? FaultId);

public sealed record VirtualExternalRequestSnapshot(
    string RequestId,
    string EndpointId,
    string Operation,
    string IdempotencyKey,
    string PayloadHash,
    VirtualExternalRequestState State,
    IReadOnlyList<VirtualExternalAttemptSnapshot> Attempts,
    bool IdempotencyReplayed,
    long StartedAtOffsetMilliseconds,
    long CompletedAtOffsetMilliseconds,
    long Version);

public sealed record VirtualExternalInvokeRequest(
    string EndpointId,
    string Operation,
    string IdempotencyKey,
    string PayloadHash,
    int MaxAttempts = 1,
    long? TimeoutMilliseconds = null,
    long RetryDelayMilliseconds = 0);

public sealed record VirtualExternalAuditRecord(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    long VirtualOffsetMilliseconds,
    string Operation,
    string Target,
    string? Detail,
    bool Success);

public sealed record VirtualExternalStatus(
    int EndpointCount,
    int ActiveFaultCount,
    int RequestCount,
    int OpenCircuitCount,
    int AuditCount,
    long OperationSequence);