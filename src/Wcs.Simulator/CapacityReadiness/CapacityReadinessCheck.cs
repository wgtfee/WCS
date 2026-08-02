namespace Wcs.Simulator.CapacityReadiness;

public sealed record CapacityReadinessCheck(
    CapacityReadinessCheckKind Kind,
    bool Passed,
    string Detail);
