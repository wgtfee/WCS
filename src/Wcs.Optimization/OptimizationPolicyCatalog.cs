namespace Wcs.Optimization;

/// <summary>
/// Governed P5 candidate catalog. These candidates are experiment inputs only and never
/// replace the production dispatch strategy automatically.
/// </summary>
public static class OptimizationPolicyCatalog
{
    public static IReadOnlyList<OptimizationPolicyCandidate> CreateStandardCandidates(
        string constraintProfileHash,
        string approvedBy,
        DateTime approvedAtUtc,
        string version = "1.0.0")
    {
        if (!OptimizationHash.IsSha256(constraintProfileHash))
            throw new ArgumentException("Constraint profile must be SHA-256.", nameof(constraintProfileHash));
        if (string.IsNullOrWhiteSpace(approvedBy))
            throw new ArgumentException("Explicit approval evidence is required.", nameof(approvedBy));
        if (approvedAtUtc == default)
            throw new ArgumentException("Approval time is required.", nameof(approvedAtUtc));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Policy version is required.", nameof(version));

        return
        [
            Candidate("current-production-baseline", OptimizationPolicyKind.CurrentProductionBaseline),
            Candidate("shortest-distance", OptimizationPolicyKind.ShortestDistance),
            Candidate("health-aware", OptimizationPolicyKind.HealthAware),
            Candidate("energy-aware", OptimizationPolicyKind.EnergyAware),
            Candidate("sla-aware", OptimizationPolicyKind.SlaAware),
            Candidate("balanced-multi-objective", OptimizationPolicyKind.BalancedMultiObjective)
        ];

        OptimizationPolicyCandidate Candidate(string id, OptimizationPolicyKind kind) => new()
        {
            PolicyId = id,
            Version = version.Trim(),
            Kind = kind,
            ConstraintProfileHash = constraintProfileHash.ToLowerInvariant(),
            ApprovedBy = approvedBy.Trim(),
            ApprovedAtUtc = approvedAtUtc.ToUniversalTime()
        };
    }
}
