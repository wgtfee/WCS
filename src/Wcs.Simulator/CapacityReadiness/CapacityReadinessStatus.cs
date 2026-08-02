namespace Wcs.Simulator.CapacityReadiness;

public sealed record CapacityReadinessStatus(
    int ProfileCount,
    int CompletedProfileCount,
    int SampleCount,
    int AuditCount,
    bool ProductionAllowed,
    bool ControlWritesAllowed,
    bool RealHilExecutedByS8);
