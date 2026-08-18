namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Core.AnomalyDetection.Maintenance;
using Wcs.Core.AnomalyDetection.RootCause;

public sealed class AssetHealthMaintenanceDecisionEngineTests
{
    [Fact]
    public void Confirmed_review_generates_actionable_inspection_recommendation()
    {
        var engine = new AssetHealthMaintenanceDecisionEngine(Options());
        var recommendation = engine.Generate(
            Analysis(RootCauseReviewDecision.Confirmed, "N-MOTOR", 0.82),
            Event(),
            DateTime.UnixEpoch.AddHours(1));

        Assert.NotNull(recommendation);
        Assert.Equal("RULE-MOTOR", recommendation!.RuleId);
        Assert.Equal("N-MOTOR", recommendation.RootCauseNodeId);
        Assert.Equal(MaintenanceRecommendationStatus.Proposed, recommendation.Status);
        Assert.Contains("Check motor current terminals", recommendation.InspectionItems);
        Assert.Contains("Lock out and tag out before inspection", recommendation.SafetyNotes);
        Assert.Contains("inspection recommendation only", recommendation.Explanation);
    }

    [Theory]
    [InlineData(RootCauseReviewDecision.Pending)]
    [InlineData(RootCauseReviewDecision.Rejected)]
    public void Unapproved_root_cause_does_not_generate_recommendation(RootCauseReviewDecision decision)
    {
        var engine = new AssetHealthMaintenanceDecisionEngine(Options());

        Assert.Null(engine.Generate(Analysis(decision, null, 0.9), Event(), DateTime.UnixEpoch));
    }

    [Fact]
    public void No_approved_rule_does_not_generate_fake_recommendation()
    {
        var options = Options();
        options.RuleSet.Rules.Clear();
        var engine = new AssetHealthMaintenanceDecisionEngine(options);

        Assert.Null(engine.Generate(
            Analysis(RootCauseReviewDecision.Confirmed, "N-MOTOR", 0.9),
            Event(),
            DateTime.UnixEpoch));
    }

    [Fact]
    public void Recommendation_id_is_deterministic_for_same_rule_analysis_and_event_version()
    {
        var engine = new AssetHealthMaintenanceDecisionEngine(Options());
        var analysis = Analysis(RootCauseReviewDecision.Confirmed, "N-MOTOR", 0.9);
        var healthEvent = Event();

        var first = engine.Generate(analysis, healthEvent, DateTime.UnixEpoch);
        var second = engine.Generate(analysis, healthEvent, DateTime.UnixEpoch.AddDays(1));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.RecommendationId, second!.RecommendationId);
        Assert.Equal(engine.RuleSetRegistration.RuleSetHash, first.RuleSetHash);
    }

    [Fact]
    public void Human_supplemented_node_can_match_exact_approved_rule_without_candidate()
    {
        var engine = new AssetHealthMaintenanceDecisionEngine(Options());
        var analysis = Analysis(RootCauseReviewDecision.Supplemented, "N-MOTOR", null, includeCandidate: false);

        var recommendation = engine.Generate(analysis, Event(), DateTime.UnixEpoch);

        Assert.NotNull(recommendation);
        Assert.Equal(RootCauseReviewDecision.Supplemented, recommendation!.RootCauseReviewDecision);
        Assert.Equal(0, recommendation.RootCauseConfidence);
        Assert.Contains("human supplemented", recommendation.Explanation);
    }

    [Fact]
    public void Confirmed_candidate_below_confidence_threshold_is_rejected()
    {
        var options = Options();
        options.MinimumRootCauseConfidence = 0.5;
        var engine = new AssetHealthMaintenanceDecisionEngine(options);

        Assert.Null(engine.Generate(
            Analysis(RootCauseReviewDecision.Confirmed, "N-MOTOR", 0.49),
            Event(),
            DateTime.UnixEpoch));
    }

    [Fact]
    public void Exact_node_rule_has_precedence_over_kind_rule()
    {
        var options = Options();
        options.RuleSet.Rules.Add(new MaintenanceDecisionRule
        {
            RuleId = "RULE-COMPONENT-GENERIC",
            RootCauseKind = RootCauseNodeKind.Component,
            MinimumEventGrade = AssetHealthGrade.Degraded,
            Title = "Generic component inspection",
            Priority = 1,
            InspectionItems = { "Generic component check" }
        });
        var engine = new AssetHealthMaintenanceDecisionEngine(options);

        var recommendation = engine.Generate(
            Analysis(RootCauseReviewDecision.Confirmed, "N-MOTOR", 0.9),
            Event(),
            DateTime.UnixEpoch);

        Assert.NotNull(recommendation);
        Assert.Equal("RULE-MOTOR", recommendation!.RuleId);
    }

    [Fact]
    public void Enabled_rule_set_requires_version_source_and_approval()
    {
        var options = Options();
        options.RuleSet.ApprovedBy = string.Empty;

        var exception = Assert.Throws<InvalidOperationException>(
            () => new AssetHealthMaintenanceDecisionEngine(options));

        Assert.Contains("approval", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AssetHealthMaintenanceOptions Options() => new()
    {
        Enabled = true,
        MaximumRules = 100,
        MaximumItemsPerRecommendation = 20,
        MinimumRootCauseConfidence = 0.25,
        RuleSet = new MaintenanceRuleSetDefinition
        {
            Version = "maintenance-v1",
            Source = "unit-test",
            ApprovedBy = "maintenance-engineer",
            ApprovedAtUtc = DateTime.UnixEpoch,
            Rules =
            {
                new MaintenanceDecisionRule
                {
                    RuleId = "RULE-MOTOR",
                    RootCauseNodeId = "N-MOTOR",
                    RootCauseKind = RootCauseNodeKind.Component,
                    MinimumEventGrade = AssetHealthGrade.Degraded,
                    Title = "Inspect motor electrical and mechanical condition",
                    Priority = 2,
                    EstimatedMinutes = 45,
                    InspectionItems =
                    {
                        "Check motor current terminals",
                        "Inspect coupling alignment",
                        "Measure bearing temperature"
                    },
                    Components = { "Motor", "Coupling", "Bearing" },
                    Tools = { "Clamp meter", "Thermal camera" },
                    SpareParts = { "Motor bearing" },
                    SafetyNotes = { "Lock out and tag out before inspection" }
                }
            }
        }
    };

    private static AssetHealthEventSnapshot Event() => new()
    {
        EventId = "E-STATION",
        EventKey = "ST-01",
        AssetId = "ST-01",
        Version = 3,
        LifecycleStatus = AssetHealthEventLifecycleStatus.Active,
        Grade = AssetHealthGrade.Critical,
        PeakGrade = AssetHealthGrade.Critical,
        HealthScore = 20,
        LowestHealthScore = 20,
        FirstDetectedUtc = DateTime.UnixEpoch,
        LastObservedUtc = DateTime.UnixEpoch.AddMinutes(1),
        Acknowledged = true,
        IsSuppressed = false,
        Reason = "station cycle timeout",
        Source = "unit-test",
        Category = "deterministic"
    };

    private static AssetHealthRootCauseAnalysisSnapshot Analysis(
        RootCauseReviewDecision decision,
        string? selectedNodeId,
        double? confidence,
        bool includeCandidate = true)
    {
        var candidates = includeCandidate
            ? new[]
            {
                new RootCauseCandidate
                {
                    NodeId = "N-MOTOR",
                    EntityId = "MOTOR-01",
                    DisplayName = "Motor 01",
                    Kind = RootCauseNodeKind.Component,
                    Confidence = confidence ?? 0,
                    CoverageScore = 1,
                    TopologyScore = 1,
                    TemporalScore = 1,
                    SeverityScore = 0.8,
                    SupportingEventCount = 3,
                    SupportingEventIds = new[] { "E-MOTOR", "E-CONVEYOR", "E-STATION" },
                    PropagationPaths = Array.Empty<RootCausePropagationPath>(),
                    Explanation = "upstream motor candidate"
                }
            }
            : Array.Empty<RootCauseCandidate>();

        return new AssetHealthRootCauseAnalysisSnapshot
        {
            AnalysisId = "ANALYSIS-1",
            TriggerEventId = "E-STATION",
            TriggerEventVersion = 3,
            TriggerAssetId = "ST-01",
            GraphVersion = "graph-v1",
            GraphHash = "graph-hash",
            WindowStartUtc = DateTime.UnixEpoch,
            WindowEndUtc = DateTime.UnixEpoch.AddMinutes(5),
            AnalyzedAtUtc = DateTime.UnixEpoch.AddMinutes(5),
            ObservedEventCount = 3,
            ObservedEventIds = new[] { "E-MOTOR", "E-CONVEYOR", "E-STATION" },
            Candidates = candidates,
            PrimaryCandidate = candidates.FirstOrDefault(),
            ReviewDecision = decision,
            ReviewedBy = decision == RootCauseReviewDecision.Pending ? null : "reviewer",
            ReviewedAtUtc = decision == RootCauseReviewDecision.Pending ? null : DateTime.UnixEpoch.AddMinutes(6),
            ReviewNote = "reviewed",
            SelectedRootCauseNodeId = selectedNodeId
        };
    }
}
