using Wcs.MaintenanceLearning;

namespace Wcs.Simulator.Tests;

public sealed class MaintenanceLearningContractTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact] public void Safety_boundary_is_zero_control() { Assert.False(MaintenanceLearningSafetyBoundary.ControlWriteAllowed); Assert.False(MaintenanceLearningSafetyBoundary.AutoTrainingAllowed); Assert.False(MaintenanceLearningSafetyBoundary.AutoModelActivationAllowed); Assert.False(MaintenanceLearningSafetyBoundary.ProductionAutomationAllowed); }
    [Fact] public void Window_hash_is_deterministic() { var a = Window(); var b = Window(); Assert.Equal(a.DefinitionHash, b.DefinitionHash); Assert.Equal(64, a.DefinitionHash.Length); }
    [Fact] public void Window_requires_strictly_increasing_ranges() { var w = Window() with { ShortWindow = TimeSpan.FromMinutes(5) }; Assert.Throws<ArgumentException>(() => w.Validate()); }
    [Fact] public void Intervention_is_idempotent() { var j = new MaintenanceLearningJournal(); var i = Intervention(); Assert.Same(j.RecordIntervention(i), j.RecordIntervention(i)); }
    [Fact] public void Intervention_requires_pre_snapshot() { var j = new MaintenanceLearningJournal(); Assert.Throws<ArgumentException>(() => j.RecordIntervention(Intervention() with { PreFeatureSnapshotId = "" })); }
    [Fact] public void Intervention_bound_is_enforced() { var j = new MaintenanceLearningJournal(new MaintenanceLearningLimits(1, 10, 2)); j.RecordIntervention(Intervention()); Assert.Throws<InvalidOperationException>(() => j.RecordIntervention(Intervention() with { InterventionId = "i-2" })); }
    [Fact] public void Outcome_requires_existing_intervention() { var j = new MaintenanceLearningJournal(); Assert.Throws<KeyNotFoundException>(() => j.RecordOutcome(Outcome())); }
    [Fact] public void Outcome_cannot_precede_maintenance() { var j = Journal(); Assert.Throws<InvalidOperationException>(() => j.RecordOutcome(Outcome() with { ObservedAt = T0.AddMinutes(10) })); }
    [Fact] public void Mes_callback_source_event_is_idempotent() { var j = Journal(); var a = j.RecordOutcome(Outcome()); var b = j.RecordOutcome(Outcome() with { OutcomeId = "other" }); Assert.Equal(a.OutcomeId, b.OutcomeId); }
    [Fact] public void Evaluation_before_completion_is_pending() { var j = Journal(); Assert.Equal(MaintenanceEvaluationStatus.Pending, j.Evaluate("i-1", Window(), T0.AddMinutes(15)).Status); }
    [Fact] public void Incomplete_observation_is_censored() { var j = Journal(); Assert.Equal(MaintenanceEvaluationStatus.Censored, j.Evaluate("i-1", Window(), T0.AddHours(12)).Status); }
    [Fact] public void Complete_window_produces_effectiveness() { var j = Journal(); j.RecordOutcome(Outcome()); var r = j.Evaluate("i-1", Window(), T0.AddDays(2)); Assert.Equal(MaintenanceEvaluationStatus.Complete, r.Status); Assert.NotNull(r.Effectiveness); }
    [Fact] public void Failure_observation_prevents_failure_avoided_label() { var j = Journal(); j.RecordOutcome(Outcome() with { FailureObserved = true }); var r = j.Evaluate("i-1", Window(), T0.AddDays(2)); Assert.False(r.Effectiveness!.FailureAvoided); }
    [Fact] public void New_training_label_must_be_pending() { var j = Journal(); Assert.Throws<InvalidOperationException>(() => j.AddLabelCandidate(Label() with { State = TrainingLabelApprovalState.Approved })); }
    [Fact] public void Unapproved_label_cannot_enter_dataset() { Assert.False(TrainingDatasetAdmission.CanEnterDataset(Label())); }
    [Fact] public void Approved_label_enters_dataset_only_after_explicit_decision() { var j = Journal(); j.AddLabelCandidate(Label()); j.DecideLabel("l-1", Approval(TrainingLabelApprovalState.Approved), "corr", "key"); Assert.Single(j.ApprovedDatasetLabels("ds")); }
    [Fact] public void Label_decision_is_idempotent() { var j = Journal(); j.AddLabelCandidate(Label()); var a = j.DecideLabel("l-1", Approval(TrainingLabelApprovalState.Approved), "corr", "key"); var b = j.DecideLabel("l-1", Approval(TrainingLabelApprovalState.Approved), "corr", "key"); Assert.Equal(a, b); Assert.Single(j.Audit()); }
    [Fact] public void Outbox_enqueue_is_idempotent_and_bounded() { var o = new MaintenanceMesOutbox(new MaintenanceLearningLimits(10, 1, 2)); var a = o.Enqueue("o1", "i-1", "k1", Hash, T0); Assert.Equal(a, o.Enqueue("o2", "i-1", "k1", Hash, T0)); Assert.Throws<InvalidOperationException>(() => o.Enqueue("o3", "i-1", "k2", Hash, T0)); }
    [Fact] public void Outbox_retry_limit_is_enforced() { var o = new MaintenanceMesOutbox(new MaintenanceLearningLimits(10, 10, 2)); o.Enqueue("o1", "i-1", "k1", Hash, T0); o.MarkAttempt("k1", T0, false, "down"); o.MarkAttempt("k1", T0.AddMinutes(1), false, "down"); Assert.Throws<InvalidOperationException>(() => o.MarkAttempt("k1", T0.AddMinutes(2), false, "down")); }
    [Fact] public void Delivered_outbox_is_removed_from_pending() { var o = new MaintenanceMesOutbox(); o.Enqueue("o1", "i-1", "k1", Hash, T0); o.MarkAttempt("k1", T0.AddMinutes(1), true, null); Assert.Empty(o.Pending()); }

    private static MaintenanceLearningJournal Journal() { var j = new MaintenanceLearningJournal(); j.RecordIntervention(Intervention()); return j; }
    private static MaintenanceIntervention Intervention() => new("i-1", "asset-1", "RGV", T0, T0.AddMinutes(30), "snap-before", "snap-after", "inspect", 100m, "operator", "corr-i");
    private static MaintenanceOutcome Outcome() => new("o-1", "i-1", T0.AddHours(3), false, 5m, 20m, null, "mes-event-1");
    private static VersionedEvaluationWindow Window() => new("RGV", "v1", TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromHours(24), "approver", T0);
    private static TrainingLabelCandidate Label() => new("l-1", "i-1", "ds", "effective", TrainingLabelApprovalState.Pending, Hash, T0.AddDays(2));
    private static TrainingLabelApproval Approval(TrainingLabelApprovalState state) => new("l-1", state, "reviewer", "verified outcome", T0.AddDays(2));
}
