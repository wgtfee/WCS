namespace Wcs.MaintenanceLearning;

public interface IMaintenanceEvaluationWindowStore
{
    Task SaveAsync(VersionedEvaluationWindow window, CancellationToken ct = default);
    Task<VersionedEvaluationWindow?> GetAsync(string assetType, string version, CancellationToken ct = default);
}

public interface IMaintenanceCausalEvidenceStore
{
    Task SaveCandidateAsync(CausalCandidate candidate, CancellationToken ct = default);
    Task SaveCounterfactualAsync(CounterfactualEstimate estimate, CancellationToken ct = default);
    Task<IReadOnlyList<CausalCandidate>> ListCandidatesAsync(string interventionId, int take = 100, CancellationToken ct = default);
}
