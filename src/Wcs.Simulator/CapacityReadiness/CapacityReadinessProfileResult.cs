namespace Wcs.Simulator.CapacityReadiness;

public sealed record CapacityReadinessProfileResult(
    CapacityProfileSnapshot Snapshot,
    IReadOnlyList<CapacitySample> Samples,
    IReadOnlyList<CapacityReadinessCheck> Checks);
