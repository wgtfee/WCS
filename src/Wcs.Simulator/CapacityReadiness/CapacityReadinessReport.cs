namespace Wcs.Simulator.CapacityReadiness;

public sealed record CapacityReadinessReport(
    string ProfileId,
    IReadOnlyList<CapacityReadinessCheck> Checks,
    HilReadinessSnapshot HilReadiness,
    DateTimeOffset CreatedAtUtc);
