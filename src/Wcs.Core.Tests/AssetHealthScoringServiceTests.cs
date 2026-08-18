namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;

public sealed class AssetHealthScoringServiceTests
{
    [Fact]
    public void Disabled_scoring_returns_no_snapshot()
    {
        var service = CreateService(enabled: false);

        var result = service.Evaluate(Snapshot("RGV-OFF", 0.9, FusedHealthStatus.Alarm));

        Assert.Null(result);
        Assert.False(service.GetStatus().Enabled);
    }

    [Fact]
    public void Normal_asset_without_evidence_scores_one_hundred()
    {
        var service = CreateService();

        var result = service.Evaluate(Snapshot("RGV-HEALTHY", 0, FusedHealthStatus.Normal));

        Assert.NotNull(result);
        Assert.Equal(100, result!.HealthScore);
        Assert.Equal(AssetHealthGrade.Healthy, result.Grade);
        Assert.Empty(result.Factors);
    }

    [Theory]
    [InlineData(0.50, FusedHealthStatus.Observe, AssetHealthGrade.Attention, 70, 85)]
    [InlineData(0.75, FusedHealthStatus.Warning, AssetHealthGrade.Degraded, 40, 70)]
    [InlineData(0.90, FusedHealthStatus.Alarm, AssetHealthGrade.Critical, 0, 40)]
    public void Risk_bands_map_to_expected_health_grades(
        double risk,
        FusedHealthStatus status,
        AssetHealthGrade expectedGrade,
        double minimumScore,
        double maximumScore)
    {
        var service = CreateService();

        var result = service.Evaluate(Snapshot("RGV-GRADE", risk, status, Evidence("RULE", 0.8)));

        Assert.NotNull(result);
        Assert.Equal(expectedGrade, result!.Grade);
        Assert.InRange(result.HealthScore, minimumScore, maximumScore);
    }

    [Fact]
    public void Factor_penalties_explain_the_total_health_deduction()
    {
        var service = CreateService();
        var result = service.Evaluate(Snapshot(
            "RGV-FACTORS",
            0.75,
            FusedHealthStatus.Warning,
            Evidence("CONSISTENCY", 0.8),
            Evidence("ML", 0.4)));

        Assert.NotNull(result);
        Assert.Equal(2, result!.Factors.Count);
        Assert.True(result.Factors[0].Penalty > result.Factors[1].Penalty);
        Assert.InRange(
            result.Factors.Sum(static factor => factor.Penalty),
            100 - result.HealthScore - 0.02,
            100 - result.HealthScore + 0.02);
    }

    [Fact]
    public void Asset_list_filters_and_orders_worst_health_first()
    {
        var engine = new StubFusionEngine(
            Snapshot("A", 0, FusedHealthStatus.Normal),
            Snapshot("B", 0.75, FusedHealthStatus.Warning, Evidence("RULE", 0.8)),
            Snapshot("C", 0.90, FusedHealthStatus.Alarm, Evidence("ML", 0.9)));
        var service = CreateService(engine: engine);

        var result = service.GetAssets(AssetHealthGrade.Degraded, 10);

        Assert.Equal(new[] { "C", "B" }, result.Select(static item => item.AssetId));
    }

    private static AssetHealthScoringService CreateService(
        bool enabled = true,
        IAnomalyFusionEngine? engine = null) => new(
        new AssetHealthScoringOptions
        {
            Enabled = enabled,
            HealthyMinimumScore = 85,
            AttentionMinimumScore = 70,
            DegradedMinimumScore = 40,
            MaximumFactors = 10
        },
        new AnomalyFusionOptions
        {
            Enabled = true,
            ObserveThreshold = 0.35,
            WarningThreshold = 0.65,
            AlarmThreshold = 0.85
        },
        engine ?? new StubFusionEngine());

    private static FusedHealthSnapshot Snapshot(
        string assetId,
        double risk,
        FusedHealthStatus status,
        params FusedEvidenceSummary[] evidence) => new()
    {
        AssetId = assetId,
        Status = status,
        Score = risk,
        IndependentSourceCount = evidence.Select(static item => item.Source).Distinct().Count(),
        FirstObservedAtUtc = DateTime.UnixEpoch,
        LastEvaluatedAtUtc = DateTime.UnixEpoch.AddMinutes(1),
        Evidence = evidence,
        Summary = null
    };

    private static FusedEvidenceSummary Evidence(string source, double contribution) => new()
    {
        EvidenceId = $"{source}-1",
        Source = source,
        Category = "TEST",
        Score = contribution,
        Confidence = 1,
        Contribution = contribution,
        Severity = PlcAnomalySeverity.Warning,
        ObservedAtUtc = DateTime.UnixEpoch,
        RelatedEntityId = null,
        Reason = $"{source} test evidence"
    };

    private sealed class StubFusionEngine : IAnomalyFusionEngine
    {
        private readonly IReadOnlyList<FusedHealthSnapshot> _snapshots;

        public StubFusionEngine(params FusedHealthSnapshot[] snapshots) =>
            _snapshots = snapshots;

        public void Process(AnomalyEvidence evidence)
        {
        }

        public void Maintenance(DateTime utcNow)
        {
        }

        public FusedHealthSnapshot? GetAsset(string assetId) =>
            _snapshots.FirstOrDefault(snapshot => snapshot.AssetId == assetId);

        public IReadOnlyList<FusedHealthSnapshot> GetAssets(
            FusedHealthStatus? minimumStatus = null,
            int maximumCount = 200) =>
            _snapshots
                .Where(snapshot => minimumStatus is null ||
                    (int)snapshot.Status >= (int)minimumStatus.Value)
                .Take(maximumCount)
                .ToArray();

        public AnomalyFusionStatus GetStatus() => new()
        {
            Enabled = true,
            TrackedAssets = _snapshots.Count
        };
    }
}
