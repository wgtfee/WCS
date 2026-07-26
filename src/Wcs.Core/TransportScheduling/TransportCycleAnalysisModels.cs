namespace Wcs.Core.TransportScheduling;

public enum TransportCycleAnomalyKind
{
    InvalidSequence = 0,
    PhaseDuration = 1,
    TotalDuration = 2
}

public sealed class TransportCycleAnalysisOptions
{
    public bool Enabled { get; set; }
    public int MinimumBaselineCycles { get; set; } = 30;
    public int MaximumBaselineCyclesPerContext { get; set; } = 500;
    public int MaximumTrackedExecutions { get; set; } = 20_000;
    public int MaximumCompletedCycles { get; set; } = 5_000;
    public int MaximumAnomalies { get; set; } = 5_000;
    public double MadMultiplier { get; set; } = 6.0;
    public double MinimumMadMilliseconds { get; set; } = 100.0;
    public double MinimumTotalDurationMilliseconds { get; set; }
    public double MinimumPhaseDurationMilliseconds { get; set; }
}

public sealed record TransportCyclePhaseDuration
{
    public required TransportExecutionState State { get; init; }
    public required DateTime EnteredAtUtc { get; init; }
    public required DateTime ExitedAtUtc { get; init; }
    public required double DurationMilliseconds { get; init; }
    public int Occurrence { get; init; }
}

public sealed record TransportCycleRecord
{
    public required string CycleId { get; init; }
    public required string RequestId { get; init; }
    public required string VehicleId { get; init; }
    public required string ContextKey { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required DateTime EndedAtUtc { get; init; }
    public required TransportExecutionState TerminalState { get; init; }
    public required double TotalDurationMilliseconds { get; init; }
    public required IReadOnlyList<TransportCyclePhaseDuration> Phases { get; init; }
    public string? LastError { get; init; }
    public bool IsSuccessful => TerminalState == TransportExecutionState.Completed;
}

public sealed record TransportCycleAnomalyRecord
{
    public required string AnomalyId { get; init; }
    public required string RequestId { get; init; }
    public required string VehicleId { get; init; }
    public required string ContextKey { get; init; }
    public required TransportCycleAnomalyKind Kind { get; init; }
    public TransportExecutionState? Phase { get; init; }
    public required DateTime DetectedAtUtc { get; init; }
    public required double ActualMilliseconds { get; init; }
    public double? MedianMilliseconds { get; init; }
    public double? ScaledMadMilliseconds { get; init; }
    public double? Deviation { get; init; }
    public required string Reason { get; init; }
}

public sealed record TransportCycleAnalysisStatus
{
    public bool Enabled { get; init; }
    public int TrackedExecutions { get; init; }
    public int CompletedCycles { get; init; }
    public int BaselineContexts { get; init; }
    public long ObservedTransitions { get; init; }
    public long SuccessfulCycles { get; init; }
    public long InterruptedCycles { get; init; }
    public long InvalidSequenceAnomalies { get; init; }
    public long DurationAnomalies { get; init; }
    public long DroppedExecutions { get; init; }
}

public interface ITransportCycleAnalysisService
{
    void Observe(
        TransportExecutionSnapshot? before,
        TransportExecutionSnapshot after,
        string operation,
        bool operationSucceeded);

    IReadOnlyList<TransportCycleRecord> GetCycles(int maximumCount = 200);
    IReadOnlyList<TransportCycleAnomalyRecord> GetAnomalies(int maximumCount = 200);
    TransportCycleAnalysisStatus GetStatus();
}
