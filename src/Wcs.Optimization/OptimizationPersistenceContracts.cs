namespace Wcs.Optimization;

public interface IOptimizationExperimentStore
{
    Task SaveDefinitionAsync(OptimizationExperimentDefinition definition, CancellationToken cancellationToken = default);
    Task SaveResultAsync(OptimizationExperimentResult result, CancellationToken cancellationToken = default);
    Task<OptimizationExperimentDefinition?> GetDefinitionAsync(string experimentId, CancellationToken cancellationToken = default);
    Task<OptimizationExperimentResult?> GetResultAsync(string experimentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OptimizationExperimentSummary>> ListAsync(int limit, CancellationToken cancellationToken = default);
}

public sealed record OptimizationExperimentSummary(
    string ExperimentId,
    string DefinitionHash,
    string SoftwareHead,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    string? EvidenceHash,
    bool ControlWriteAllowed,
    bool AutoProductionPolicyReplacementAllowed);

public sealed record OptimizationRecoveryResult(
    int DefinitionCount,
    int CompletedResultCount,
    int InvalidDefinitionCount,
    bool Healthy,
    IReadOnlyList<string> Errors);

public interface IOptimizationRecoveryService
{
    Task<OptimizationRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default);
}
