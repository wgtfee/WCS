namespace Wcs.Simulator.VirtualPlc;

public sealed class VirtualPlcOptions
{
    public const string SectionName = "SimulationVirtualPlc";

    public int MaximumBlocks { get; set; } = 128;
    public int MaximumBlockBytes { get; set; } = 65_536;
    public int MaximumOperationBytes { get; set; } = 65_536;
    public int MaximumScenarioTransferBytes { get; set; } = 1_536;
    public int MaximumFaults { get; set; } = 1_024;
    public int MaximumFaultPayloadBytes { get; set; } = 1_536;
    public int MaximumAuditRecords { get; set; } = 1_000;

    public void Validate()
    {
        if (MaximumBlocks is < 1 or > 4_096)
            throw new InvalidOperationException("SimulationVirtualPlc.MaximumBlocks must be between 1 and 4,096.");
        if (MaximumBlockBytes is < 1 or > 1_048_576)
            throw new InvalidOperationException("SimulationVirtualPlc.MaximumBlockBytes must be between 1 and 1,048,576.");
        if (MaximumOperationBytes is < 1 || MaximumOperationBytes > MaximumBlockBytes)
            throw new InvalidOperationException("SimulationVirtualPlc.MaximumOperationBytes must be between 1 and MaximumBlockBytes.");
        if (MaximumScenarioTransferBytes is < 1 || MaximumScenarioTransferBytes > MaximumOperationBytes)
            throw new InvalidOperationException("SimulationVirtualPlc.MaximumScenarioTransferBytes must be between 1 and MaximumOperationBytes.");
        if (MaximumFaults is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualPlc.MaximumFaults must be between 1 and 100,000.");
        if (MaximumFaultPayloadBytes is < 1 || MaximumFaultPayloadBytes > MaximumOperationBytes)
            throw new InvalidOperationException("SimulationVirtualPlc.MaximumFaultPayloadBytes must be between 1 and MaximumOperationBytes.");
        if (MaximumAuditRecords is < 1 or > 100_000)
            throw new InvalidOperationException("SimulationVirtualPlc.MaximumAuditRecords must be between 1 and 100,000.");
    }
}

public enum VirtualPlcFaultKind
{
    Disconnect,
    Timeout,
    ReadFailure,
    WriteFailure,
    Stuck,
    BitFlip,
    Jitter,
    OutOfRange
}

public sealed class VirtualPlcFaultDefinition
{
    public string Id { get; set; } = string.Empty;
    public VirtualPlcFaultKind Kind { get; set; }
    public string Target { get; set; } = string.Empty;
    public long StartMilliseconds { get; set; }
    public long? EndMilliseconds { get; set; }
    public int Offset { get; set; }
    public int Length { get; set; } = 1;
    public int BitIndex { get; set; }
    public int JitterMinimum { get; set; } = -1;
    public int JitterMaximum { get; set; } = 1;
    public byte[]? ReplacementBytes { get; set; }
}

public sealed record VirtualPlcFaultSnapshot(
    string Id,
    VirtualPlcFaultKind Kind,
    string Target,
    long StartMilliseconds,
    long? EndMilliseconds,
    int Offset,
    int Length,
    int BitIndex,
    int JitterMinimum,
    int JitterMaximum,
    byte[]? ReplacementBytes,
    byte[]? FrozenBytes,
    bool Enabled,
    bool Active);

public sealed record VirtualPlcOperationResult(
    long Sequence,
    string Operation,
    string Target,
    bool Success,
    bool TimedOut,
    string? ErrorCode,
    string? ErrorMessage,
    int Offset,
    int Count,
    byte[] Data,
    IReadOnlyList<string> AppliedFaultIds);

public sealed record VirtualPlcBlockSnapshot(
    string BlockKey,
    string PlcName,
    int DbNumber,
    int Size,
    string Sha256,
    byte[] Data);

public sealed record VirtualPlcAuditRecord(
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    long VirtualOffsetMilliseconds,
    string Operation,
    string Target,
    bool Success,
    bool TimedOut,
    string? ErrorCode,
    int Offset,
    int Count,
    string BeforeSha256,
    string AfterSha256,
    IReadOnlyList<string> AppliedFaultIds);

public sealed record VirtualPlcStatusSnapshot(
    int BlockCount,
    int FaultCount,
    int ActiveFaultCount,
    int AuditCount,
    long OperationSequence,
    IReadOnlyDictionary<string, bool> Connections,
    IReadOnlyList<string> Blocks);
