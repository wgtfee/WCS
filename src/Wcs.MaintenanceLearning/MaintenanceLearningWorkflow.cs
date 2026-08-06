namespace Wcs.MaintenanceLearning;

public sealed record MaintenanceClosedLoopSample(
    MaintenanceIntervention Intervention,
    MaintenanceOutcome Outcome,
    MaintenanceEvaluationResult Evaluation,
    TrainingLabelCandidate Label,
    bool DatasetAdmitted,
    bool ControlWriteAllowed = false,
    bool AutoTrainingAllowed = false,
    bool AutoModelActivationAllowed = false);

/// <summary>
/// Coordinates the P4 evidence/learning path only. This workflow never emits equipment,
/// scheduling, traffic, route-reservation or model-activation commands.
/// </summary>
public sealed class MaintenanceLearningWorkflow
{
    private readonly MaintenanceLearningJournal _journal;
    private readonly MaintenanceMesOutbox _outbox;
    private readonly IMaintenanceLearningStore _store;

    public MaintenanceLearningWorkflow(
        IMaintenanceLearningStore store,
        MaintenanceLearningLimits? limits = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _journal = new MaintenanceLearningJournal(limits);
        _outbox = new MaintenanceMesOutbox(limits);
    }

    public async Task<MaintenanceIntervention> RecordInterventionAsync(
        MaintenanceIntervention intervention,
        string mesPayloadHash,
        string outboxId,
        string idempotencyKey,
        DateTimeOffset utc,
        CancellationToken ct = default)
    {
        var recorded = _journal.RecordIntervention(intervention);
        await _store.SaveInterventionAsync(recorded, ct);
        var outbox = _outbox.Enqueue(outboxId, recorded.InterventionId, idempotencyKey, mesPayloadHash, utc);
        await _store.SaveOutboxAsync(outbox, ct);
        return recorded;
    }

    public async Task<MaintenanceOutcome> RecordOutcomeAsync(
        MaintenanceOutcome outcome,
        CancellationToken ct = default)
    {
        var recorded = _journal.RecordOutcome(outcome);
        await _store.SaveOutcomeAsync(recorded, ct);
        return recorded;
    }

    public async Task<MaintenanceEvaluationResult> EvaluateAsync(
        string interventionId,
        VersionedEvaluationWindow window,
        DateTimeOffset asOfUtc,
        CancellationToken ct = default)
    {
        var result = _journal.Evaluate(interventionId, window, asOfUtc);
        await _store.SaveEvaluationAsync(result, ct);
        return result;
    }

    public async Task<TrainingLabelCandidate> AddLabelCandidateAsync(
        TrainingLabelCandidate candidate,
        CancellationToken ct = default)
    {
        var added = _journal.AddLabelCandidate(candidate);
        await _store.SaveLabelAsync(added, ct);
        return added;
    }

    public async Task<TrainingLabelCandidate> DecideLabelAsync(
        string labelId,
        TrainingLabelApproval approval,
        string correlationId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var decided = _journal.DecideLabel(labelId, approval, correlationId, idempotencyKey);
        await _store.SaveApprovalAsync(approval, correlationId, idempotencyKey, ct);
        return decided;
    }

    public async Task<MesOutboxEntry> RecordMesAttemptAsync(
        string idempotencyKey,
        DateTimeOffset utc,
        bool delivered,
        string? error,
        CancellationToken ct = default)
    {
        var entry = _outbox.MarkAttempt(idempotencyKey, utc, delivered, error);
        await _store.SaveOutboxAsync(entry, ct);
        return entry;
    }

    public IReadOnlyList<TrainingLabelCandidate> ApprovedDatasetLabels(string datasetKey, int take = 1000) =>
        _journal.ApprovedDatasetLabels(datasetKey, take);

    public static MaintenanceClosedLoopSample BuildClosedLoopSample(
        MaintenanceIntervention intervention,
        MaintenanceOutcome outcome,
        MaintenanceEvaluationResult evaluation,
        TrainingLabelCandidate label)
    {
        if (!string.Equals(intervention.InterventionId, outcome.InterventionId, StringComparison.Ordinal) ||
            !string.Equals(intervention.InterventionId, evaluation.InterventionId, StringComparison.Ordinal) ||
            !string.Equals(intervention.InterventionId, label.InterventionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Closed-loop sample references must share the same intervention.");
        if (outcome.ObservedAt < intervention.CompletedAt)
            throw new InvalidOperationException("Outcome cannot precede intervention completion.");
        if (evaluation.Status != MaintenanceEvaluationStatus.Complete || evaluation.Effectiveness is null)
            throw new InvalidOperationException("Closed-loop sample requires a completed effectiveness evaluation.");

        return new MaintenanceClosedLoopSample(
            intervention,
            outcome,
            evaluation,
            label,
            TrainingDatasetAdmission.CanEnterDataset(label));
    }
}
