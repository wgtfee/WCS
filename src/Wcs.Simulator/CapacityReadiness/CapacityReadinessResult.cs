namespace Wcs.Simulator.CapacityReadiness;

public sealed record CapacityReadinessResult(
    CapacityProfileSnapshot Profile,
    IReadOnlyList<CapacitySample> Samples,
    HilReadinessSnapshot Readiness,
    string FinalStateHash,
    string EvidenceHash);
