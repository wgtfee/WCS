using System.Security.Cryptography;
using System.Text;

namespace Wcs.MaintenanceLearning;

public enum MaintenanceEvaluationStatus
{
    Pending,
    Censored,
    Complete
}

public sealed record MaintenanceLearningLimits(
    int MaximumInterventions = 10000,
    int MaximumOutboxEntries = 10000,
    int MaximumRetryCount = 8)
{
    public void Validate()
    {
        if (MaximumInterventions is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumInterventions));
        if (MaximumOutboxEntries is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumOutboxEntries));
        if (MaximumRetryCount is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(MaximumRetryCount));
    }
}

public sealed record VersionedEvaluationWindow(
    string AssetType,
    string Version,
    TimeSpan ImmediateWindow,
    TimeSpan ShortWindow,
    TimeSpan MediumWindow,
    TimeSpan LongWindow,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AssetType)) throw new ArgumentException("AssetType is required.", nameof(AssetType));
        if (string.IsNullOrWhiteSpace(Version)) throw new ArgumentException("Version is required.", nameof(Version));
        if (string.IsNullOrWhiteSpace(ApprovedBy)) throw new ArgumentException("ApprovedBy is required.", nameof(ApprovedBy));
        if (ImmediateWindow <= TimeSpan.Zero || ShortWindow <= ImmediateWindow || MediumWindow <= ShortWindow || LongWindow <= MediumWindow)
            throw new ArgumentException("Evaluation windows must be positive and strictly increasing.");
    }

    public string DefinitionHash => MaintenanceLearningHash.Sha256(
        AssetType.Trim(), Version.Trim(), ImmediateWindow.Ticks.ToString(), ShortWindow.Ticks.ToString(),
        MediumWindow.Ticks.ToString(), LongWindow.Ticks.ToString(), ApprovedBy.Trim(), ApprovedAtUtc.ToUniversalTime().ToString("O"));
}

public sealed record MaintenanceEvaluationResult(
    string InterventionId,
    MaintenanceEvaluationStatus Status,
    string WindowVersion,
    DateTimeOffset EvaluatedAtUtc,
    MaintenanceEffectiveness? Effectiveness,
    string Reason,
    string EvidenceHash);

public sealed record MaintenanceAuditEntry(
    string Action,
    string ReferenceId,
    string Actor,
    string Reason,
    string CorrelationId,
    string IdempotencyKey,
    DateTimeOffset Utc,
    string EntryHash);

public sealed record MesOutboxEntry(
    string OutboxId,
    string InterventionId,
    string IdempotencyKey,
    string PayloadHash,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    string? LastError)
{
    public bool Delivered => DeliveredAtUtc.HasValue;
}

public sealed class MaintenanceLearningJournal
{
    private readonly object _gate = new();
    private readonly MaintenanceLearningLimits _limits;
    private readonly Dictionary<string, MaintenanceIntervention> _interventions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MaintenanceOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrainingLabelCandidate> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrainingLabelApproval> _approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MaintenanceAuditEntry> _idempotency = new(StringComparer.Ordinal);
    private readonly List<MaintenanceAuditEntry> _audit = [];

    public MaintenanceLearningJournal(MaintenanceLearningLimits? limits = null)
    {
        _limits = limits ?? new MaintenanceLearningLimits();
        _limits.Validate();
    }

    public MaintenanceIntervention RecordIntervention(MaintenanceIntervention intervention)
    {
        ArgumentNullException.ThrowIfNull(intervention);
        ValidateIntervention(intervention);
        lock (_gate)
        {
            if (_interventions.TryGetValue(intervention.InterventionId, out var existing)) return existing;
            if (_interventions.Count >= _limits.MaximumInterventions) throw new InvalidOperationException("Maximum intervention bound reached.");
            _interventions.Add(intervention.InterventionId, intervention);
            return intervention;
        }
    }

    public MaintenanceOutcome RecordOutcome(MaintenanceOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_gate)
        {
            if (!_interventions.TryGetValue(outcome.InterventionId, out var intervention)) throw new KeyNotFoundException("Intervention not found.");
            if (outcome.ObservedAt < intervention.CompletedAt) throw new InvalidOperationException("Outcome cannot precede intervention completion.");
            if (string.IsNullOrWhiteSpace(outcome.SourceEventId)) throw new ArgumentException("SourceEventId is required.", nameof(outcome));
            if (_sourceEvents.TryGetValue(outcome.SourceEventId, out var existingId)) return _outcomes[existingId];
            if (_outcomes.TryGetValue(outcome.OutcomeId, out var existing)) return existing;
            _outcomes.Add(outcome.OutcomeId, outcome);
            _sourceEvents.Add(outcome.SourceEventId, outcome.OutcomeId);
            return outcome;
        }
    }

    public MaintenanceEvaluationResult Evaluate(string interventionId, VersionedEvaluationWindow window, DateTimeOffset asOfUtc)
    {
        window.Validate();
        lock (_gate)
        {
            if (!_interventions.TryGetValue(interventionId, out var intervention)) throw new KeyNotFoundException("Intervention not found.");
            if (!StringComparer.Ordinal.Equals(intervention.AssetType, window.AssetType)) throw new InvalidOperationException("Evaluation window asset type mismatch.");
            var eligibleAt = intervention.CompletedAt + window.LongWindow;
            if (asOfUtc < intervention.CompletedAt)
                return Result(intervention, window, asOfUtc, MaintenanceEvaluationStatus.Pending, null, "Observation has not started.");
            if (asOfUtc < eligibleAt)
                return Result(intervention, window, asOfUtc, MaintenanceEvaluationStatus.Censored, null, "Observation window is incomplete.");

            var observations = _outcomes.Values.Where(x => x.InterventionId == interventionId && x.ObservedAt <= asOfUtc).OrderBy(x => x.ObservedAt).ToArray();
            var failureObserved = observations.Any(x => x.FailureObserved);
            var actualCost = observations.Sum(x => x.ActualCost);
            var downtime = observations.Sum(x => x.DowntimeMinutes);
            var evidence = MaintenanceLearningHash.Sha256(interventionId, window.DefinitionHash, asOfUtc.ToUniversalTime().ToString("O"), failureObserved.ToString(), actualCost.ToString(), downtime.ToString());
            var effectiveness = new MaintenanceEffectiveness(interventionId, window.Version, -downtime, intervention.Cost - actualCost, !failureObserved, evidence);
            return Result(intervention, window, asOfUtc, MaintenanceEvaluationStatus.Complete, effectiveness, "Observation window complete.");
        }
    }

    public TrainingLabelCandidate AddLabelCandidate(TrainingLabelCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.State != TrainingLabelApprovalState.Pending) throw new InvalidOperationException("New training labels must start Pending.");
        if (string.IsNullOrWhiteSpace(candidate.EvidenceHash) || candidate.EvidenceHash.Length != 64) throw new ArgumentException("EvidenceHash must be SHA-256 hex.");
        lock (_gate)
        {
            if (!_interventions.ContainsKey(candidate.InterventionId)) throw new KeyNotFoundException("Intervention not found.");
            if (_labels.TryGetValue(candidate.LabelId, out var existing)) return existing;
            _labels.Add(candidate.LabelId, candidate);
            return candidate;
        }
    }

    public TrainingLabelCandidate DecideLabel(string labelId, TrainingLabelApproval approval, string correlationId, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(approval.Actor)) throw new ArgumentException("Actor is required.", nameof(approval));
        if (string.IsNullOrWhiteSpace(approval.Reason)) throw new ArgumentException("Reason is required.", nameof(approval));
        if (approval.State == TrainingLabelApprovalState.Pending) throw new InvalidOperationException("Decision cannot remain Pending.");
        if (string.IsNullOrWhiteSpace(correlationId) || string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("CorrelationId and IdempotencyKey are required.");
        lock (_gate)
        {
            if (_idempotency.TryGetValue(idempotencyKey, out _)) return _labels[labelId];
            if (!_labels.TryGetValue(labelId, out var label)) throw new KeyNotFoundException("Label not found.");
            if (label.State != TrainingLabelApprovalState.Pending) throw new InvalidOperationException("Label has already been decided.");
            var updated = label with { State = approval.State };
            _labels[labelId] = updated;
            _approvals[labelId] = approval;
            AppendAudit("TrainingLabelDecision", labelId, approval.Actor, approval.Reason, correlationId, idempotencyKey, approval.DecidedAt);
            return updated;
        }
    }

    public IReadOnlyList<TrainingLabelCandidate> ApprovedDatasetLabels(string datasetKey, int take = 1000)
    {
        if (take is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(take));
        lock (_gate)
            return _labels.Values.Where(x => x.DatasetKey == datasetKey && TrainingDatasetAdmission.CanEnterDataset(x)).OrderBy(x => x.CreatedAt).Take(take).ToArray();
    }

    public IReadOnlyList<MaintenanceAuditEntry> Audit()
    {
        lock (_gate) return _audit.ToArray();
    }

    private MaintenanceAuditEntry AppendAudit(string action, string referenceId, string actor, string reason, string correlationId, string idempotencyKey, DateTimeOffset utc)
    {
        var entry = new MaintenanceAuditEntry(action, referenceId, actor.Trim(), reason.Trim(), correlationId, idempotencyKey, utc,
            MaintenanceLearningHash.Sha256(action, referenceId, actor.Trim(), reason.Trim(), correlationId, idempotencyKey, utc.ToUniversalTime().ToString("O")));
        _idempotency.Add(idempotencyKey, entry);
        _audit.Add(entry);
        return entry;
    }

    private static MaintenanceEvaluationResult Result(MaintenanceIntervention intervention, VersionedEvaluationWindow window, DateTimeOffset utc, MaintenanceEvaluationStatus status, MaintenanceEffectiveness? effectiveness, string reason)
    {
        var hash = MaintenanceLearningHash.Sha256(intervention.InterventionId, window.DefinitionHash, utc.ToUniversalTime().ToString("O"), status.ToString(), reason, effectiveness?.EvidenceHash ?? string.Empty);
        return new MaintenanceEvaluationResult(intervention.InterventionId, status, window.Version, utc, effectiveness, reason, hash);
    }

    private static void ValidateIntervention(MaintenanceIntervention intervention)
    {
        if (string.IsNullOrWhiteSpace(intervention.InterventionId) || string.IsNullOrWhiteSpace(intervention.AssetId) || string.IsNullOrWhiteSpace(intervention.AssetType))
            throw new ArgumentException("Intervention identity is required.");
        if (intervention.CompletedAt < intervention.StartedAt) throw new ArgumentException("CompletedAt cannot precede StartedAt.");
        if (string.IsNullOrWhiteSpace(intervention.PreFeatureSnapshotId)) throw new ArgumentException("PreFeatureSnapshotId is required.");
        if (string.IsNullOrWhiteSpace(intervention.Actor) || string.IsNullOrWhiteSpace(intervention.CorrelationId)) throw new ArgumentException("Actor and CorrelationId are required.");
        if (intervention.Cost < 0) throw new ArgumentOutOfRangeException(nameof(intervention.Cost));
    }
}

public sealed class MaintenanceMesOutbox
{
    private readonly object _gate = new();
    private readonly MaintenanceLearningLimits _limits;
    private readonly Dictionary<string, MesOutboxEntry> _byKey = new(StringComparer.Ordinal);

    public MaintenanceMesOutbox(MaintenanceLearningLimits? limits = null)
    {
        _limits = limits ?? new MaintenanceLearningLimits();
        _limits.Validate();
    }

    public MesOutboxEntry Enqueue(string outboxId, string interventionId, string idempotencyKey, string payloadHash, DateTimeOffset utc)
    {
        if (string.IsNullOrWhiteSpace(outboxId) || string.IsNullOrWhiteSpace(interventionId) || string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Outbox identity is required.");
        if (payloadHash.Length != 64) throw new ArgumentException("PayloadHash must be SHA-256 hex.", nameof(payloadHash));
        lock (_gate)
        {
            if (_byKey.TryGetValue(idempotencyKey, out var replay)) return replay;
            if (_byKey.Count >= _limits.MaximumOutboxEntries) throw new InvalidOperationException("Maximum outbox bound reached.");
            var entry = new MesOutboxEntry(outboxId, interventionId, idempotencyKey, payloadHash, 0, utc, null, null, null);
            _byKey.Add(idempotencyKey, entry);
            return entry;
        }
    }

    public MesOutboxEntry MarkAttempt(string idempotencyKey, DateTimeOffset utc, bool delivered, string? error)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(idempotencyKey, out var entry)) throw new KeyNotFoundException("Outbox entry not found.");
            if (entry.Delivered) return entry;
            var attempts = entry.AttemptCount + 1;
            if (attempts > _limits.MaximumRetryCount) throw new InvalidOperationException("Maximum retry bound reached.");
            var updated = entry with { AttemptCount = attempts, LastAttemptAtUtc = utc, DeliveredAtUtc = delivered ? utc : null, LastError = delivered ? null : error };
            _byKey[idempotencyKey] = updated;
            return updated;
        }
    }

    public IReadOnlyList<MesOutboxEntry> Pending(int take = 100)
    {
        if (take is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(take));
        lock (_gate) return _byKey.Values.Where(x => !x.Delivered).OrderBy(x => x.CreatedAtUtc).Take(take).ToArray();
    }
}

public static class MaintenanceLearningSafetyBoundary
{
    public static bool ControlWriteAllowed => false;
    public static bool AutoTrainingAllowed => false;
    public static bool AutoModelActivationAllowed => false;
    public static bool ProductionAutomationAllowed => false;
}

public static class MaintenanceLearningHash
{
    public static string Sha256(params string[] parts)
    {
        var canonical = string.Join("\u001f", parts.Select(x => x ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
