namespace Wcs.MaintenanceLearning;

public interface IMaintenanceLearningStore
{
    Task SaveInterventionAsync(MaintenanceIntervention intervention, CancellationToken ct = default);
    Task SaveOutcomeAsync(MaintenanceOutcome outcome, CancellationToken ct = default);
    Task SaveEvaluationAsync(MaintenanceEvaluationResult evaluation, CancellationToken ct = default);
    Task SaveLabelAsync(TrainingLabelCandidate label, CancellationToken ct = default);
    Task SaveApprovalAsync(TrainingLabelApproval approval, string correlationId, string idempotencyKey, CancellationToken ct = default);
    Task SaveOutboxAsync(MesOutboxEntry entry, CancellationToken ct = default);
    Task<MaintenanceIntervention?> GetInterventionAsync(string interventionId, CancellationToken ct = default);
    Task<IReadOnlyList<MesOutboxEntry>> LoadPendingOutboxAsync(int take, CancellationToken ct = default);
}

public interface IMaintenanceLearningRecovery
{
    Task<MaintenanceLearningRecoverySnapshot> RecoverAsync(CancellationToken ct = default);
}

public sealed record MaintenanceLearningRecoverySnapshot(
    int InterventionCount,
    int PendingOutboxCount,
    int PendingLabelCount,
    string StateHash,
    bool ControlWriteAllowed = false,
    bool AutoTrainingAllowed = false,
    bool AutoModelActivationAllowed = false);
