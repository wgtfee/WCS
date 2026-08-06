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

    [Fact]
    public async Task Workflow_persists_intervention_and_outbox_without_control_write()
    {
        var store = new MemoryStore();
        var workflow = new MaintenanceLearningWorkflow(store);
        await workflow.RecordInterventionAsync(Intervention(), Hash, "ob-1", "mes-key-1", T0);
        Assert.Single(store.Interventions);
        Assert.Single(store.Outbox);
        Assert.False(MaintenanceLearningSafetyBoundary.ControlWriteAllowed);
    }

    [Fact]
    public async Task Workflow_persists_mes_outcome_idempotently()
    {
        var store = new MemoryStore();
        var workflow = new MaintenanceLearningWorkflow(store);
        await workflow.RecordInterventionAsync(Intervention(), Hash, "ob-1", "mes-key-1", T0);
        var first = await workflow.RecordOutcomeAsync(Outcome());
        var replay = await workflow.RecordOutcomeAsync(Outcome() with { OutcomeId = "different" });
        Assert.Equal(first.OutcomeId, replay.OutcomeId);
        Assert.Equal(2, store.Outcomes.Count);
        Assert.All(store.Outcomes, x => Assert.Equal(first.OutcomeId, x.OutcomeId));
    }

    [Fact]
    public async Task Workflow_requires_explicit_label_approval_before_dataset_admission()
    {
        var store = new MemoryStore();
        var workflow = new MaintenanceLearningWorkflow(store);
        await workflow.RecordInterventionAsync(Intervention(), Hash, "ob-1", "mes-key-1", T0);
        await workflow.AddLabelCandidateAsync(Label());
        Assert.Empty(workflow.ApprovedDatasetLabels("ds"));
        var approved = await workflow.DecideLabelAsync("l-1", Approval(TrainingLabelApprovalState.Approved), "approval-corr", "approval-key");
        Assert.True(TrainingDatasetAdmission.CanEnterDataset(approved));
        Assert.Single(workflow.ApprovedDatasetLabels("ds"));
        Assert.Single(store.Approvals);
    }

    [Fact]
    public async Task Closed_loop_sample_links_intervention_outcome_effectiveness_and_approved_label()
    {
        var store = new MemoryStore();
        var workflow = new MaintenanceLearningWorkflow(store);
        var intervention = await workflow.RecordInterventionAsync(Intervention(), Hash, "ob-1", "mes-key-1", T0);
        var outcome = await workflow.RecordOutcomeAsync(Outcome());
        var evaluation = await workflow.EvaluateAsync(intervention.InterventionId, Window(), T0.AddDays(2));
        await workflow.AddLabelCandidateAsync(Label());
        var label = await workflow.DecideLabelAsync("l-1", Approval(TrainingLabelApprovalState.Approved), "approval-corr", "approval-key");
        var sample = MaintenanceLearningWorkflow.BuildClosedLoopSample(intervention, outcome, evaluation, label);
        Assert.True(sample.DatasetAdmitted);
        Assert.False(sample.ControlWriteAllowed);
        Assert.False(sample.AutoTrainingAllowed);
        Assert.False(sample.AutoModelActivationAllowed);
    }

    private static MaintenanceLearningJournal Journal() { var j = new MaintenanceLearningJournal(); j.RecordIntervention(Intervention()); return j; }
    private static MaintenanceIntervention Intervention() => new("i-1", "asset-1", "RGV", T0, T0.AddMinutes(30), "snap-before", "snap-after", "inspect", 100m, "operator", "corr-i");
    private static MaintenanceOutcome Outcome() => new("o-1", "i-1", T0.AddHours(3), false, 5m, 20m, null, "mes-event-1");
    private static VersionedEvaluationWindow Window() => new("RGV", "v1", TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromHours(24), "approver", T0);
    private static TrainingLabelCandidate Label() => new("l-1", "i-1", "ds", "effective", TrainingLabelApprovalState.Pending, Hash, T0.AddDays(2));
    private static TrainingLabelApproval Approval(TrainingLabelApprovalState state) => new("l-1", state, "reviewer", "verified outcome", T0.AddDays(2));

    private sealed class MemoryStore : IMaintenanceLearningStore
    {
        public List<MaintenanceIntervention> Interventions { get; } = [];
        public List<MaintenanceOutcome> Outcomes { get; } = [];
        public List<MaintenanceEvaluationResult> Evaluations { get; } = [];
        public List<TrainingLabelCandidate> Labels { get; } = [];
        public List<TrainingLabelApproval> Approvals { get; } = [];
        public List<MesOutboxEntry> Outbox { get; } = [];

        public Task SaveInterventionAsync(MaintenanceIntervention intervention, CancellationToken ct = default) { Interventions.Add(intervention); return Task.CompletedTask; }
        public Task SaveOutcomeAsync(MaintenanceOutcome outcome, CancellationToken ct = default) { Outcomes.Add(outcome); return Task.CompletedTask; }
        public Task SaveEvaluationAsync(MaintenanceEvaluationResult evaluation, CancellationToken ct = default) { Evaluations.Add(evaluation); return Task.CompletedTask; }
        public Task SaveLabelAsync(TrainingLabelCandidate label, CancellationToken ct = default) { Labels.Add(label); return Task.CompletedTask; }
        public Task SaveApprovalAsync(TrainingLabelApproval approval, string correlationId, string idempotencyKey, CancellationToken ct = default) { Approvals.Add(approval); return Task.CompletedTask; }
        public Task SaveOutboxAsync(MesOutboxEntry entry, CancellationToken ct = default) { Outbox.Add(entry); return Task.CompletedTask; }
        public Task<MaintenanceIntervention?> GetInterventionAsync(string interventionId, CancellationToken ct = default) => Task.FromResult(Interventions.FirstOrDefault(x => x.InterventionId == interventionId));
        public Task<IReadOnlyList<MesOutboxEntry>> LoadPendingOutboxAsync(int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MesOutboxEntry>>(Outbox.Where(x => !x.Delivered).Take(take).ToArray());
    }
}
