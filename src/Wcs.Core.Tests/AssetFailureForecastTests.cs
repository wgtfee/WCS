namespace Wcs.Core.Tests;

using System.Security.Cryptography;
using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Core.AnomalyDetection.Fusion;
using Wcs.Core.AnomalyDetection.HealthScoring;

public sealed class AssetFailureForecastTests
{
    [Fact]
    public void Feature_builder_requires_governed_history_depth_and_span()
    {
        var options = new AssetFailureForecastOptions
        {
            MinimumHistoryPoints = 4,
            MinimumHistorySpanHours = 3,
            MaximumHistoryPoints = 100
        };
        var insufficient = CreateHistory(3, TimeSpan.FromHours(1));

        var built = AssetFailureForecastFeatureBuilder.TryBuild(
            "MOTOR-1",
            insufficient,
            options,
            out var vector,
            out var reason);

        Assert.False(built);
        Assert.Null(vector);
        Assert.Contains("At least 4 retained", reason, StringComparison.Ordinal);

        var valid = CreateHistory(4, TimeSpan.FromHours(1));
        Assert.True(AssetFailureForecastFeatureBuilder.TryBuild(
            "MOTOR-1",
            valid,
            options,
            out vector,
            out reason));
        Assert.Null(reason);
        Assert.NotNull(vector);
        Assert.Equal(AssetFailureForecastFeatureSchema.Names, vector.FeatureNames);
        Assert.Equal(14, vector.Values.Count);
        Assert.Equal(4, vector.SampleCount);
        Assert.Equal(3, vector.HistorySpanHours, 6);
    }

    [Fact]
    public void Feature_builder_produces_deterministic_degradation_features()
    {
        var options = new AssetFailureForecastOptions
        {
            MinimumHistoryPoints = 4,
            MinimumHistorySpanHours = 3,
            MaximumHistoryPoints = 100
        };
        var history = CreateHistory(4, TimeSpan.FromHours(1));

        Assert.True(AssetFailureForecastFeatureBuilder.TryBuild(
            "MOTOR-1", history, options, out var vector, out _));

        Assert.NotNull(vector);
        var values = vector.Values.ToArray();
        Assert.Equal(76, values[0], 6);
        Assert.Equal(82, values[1], 6);
        Assert.Equal(76, values[2], 6);
        Assert.Equal(88, values[3], 6);
        Assert.Equal(-4, values[5], 6);
        Assert.Equal(-12, values[6], 6);
        Assert.Equal(4, values[12], 6);
        Assert.Equal(3, values[13], 6);
    }

    [Fact]
    public void Manifest_requires_real_failure_and_censoring_evidence()
    {
        var options = new AssetFailureForecastOptions
        {
            MinimumTrainingAssets = 30,
            MinimumFailureEvents = 10,
            MinimumValidationAuc = 0.65,
            MaximumValidationBrierScore = 0.30,
            MinimumPredictionIntervalCoverage = 0.70
        };
        var manifest = CreateManifest();
        AssetFailureForecastManifestValidator.Validate(manifest, options);

        manifest.FailureEventCount = 0;
        Assert.Throws<InvalidOperationException>(() =>
            AssetFailureForecastManifestValidator.Validate(manifest, options));

        manifest = CreateManifest();
        manifest.CensoredRecordCount = 0;
        Assert.Throws<InvalidOperationException>(() =>
            AssetFailureForecastManifestValidator.Validate(manifest, options));

        manifest = CreateManifest();
        manifest.ValidationAuc = 0.60;
        Assert.Throws<InvalidOperationException>(() =>
            AssetFailureForecastManifestValidator.Validate(manifest, options));
    }

    [Fact]
    public void Manifest_hash_and_forecast_id_are_deterministic()
    {
        var manifest = CreateManifest();
        var first = AssetFailureForecastManifestValidator.ComputeManifestHash(manifest);
        var second = AssetFailureForecastManifestValidator.ComputeManifestHash(manifest);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);

        var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var id1 = AssetFailureForecastIdentity.CreateForecastId("MOTOR-1", "rul-v1", start, start.AddDays(1));
        var id2 = AssetFailureForecastIdentity.CreateForecastId("MOTOR-1", "rul-v1", start, start.AddDays(1));
        Assert.Equal(64, id1.Length);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Output_requires_monotonic_probability_and_ordered_rul_interval()
    {
        var valid = new AssetFailureForecastOutput
        {
            FailureProbability24Hours = 0.10,
            FailureProbability72Hours = 0.25,
            FailureProbability168Hours = 0.50,
            RulLowerHours = 40,
            RulMedianHours = 72,
            RulUpperHours = 120
        };
        Assert.Same(valid, AssetFailureForecastManifestValidator.ValidateOutput(valid, 1_000));

        var decreasingProbability = valid with
        {
            FailureProbability24Hours = 0.40,
            FailureProbability72Hours = 0.20
        };
        Assert.Throws<InvalidOperationException>(() =>
            AssetFailureForecastManifestValidator.ValidateOutput(decreasingProbability, 1_000));

        var invertedRul = valid with
        {
            RulLowerHours = 100,
            RulMedianHours = 50
        };
        Assert.Throws<InvalidOperationException>(() =>
            AssetFailureForecastManifestValidator.ValidateOutput(invertedRul, 1_000));
    }

    private static IReadOnlyList<AssetHealthScorePoint> CreateHistory(int count, TimeSpan interval)
    {
        var start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var score = 88 - index * 4;
                var grade = score >= 85
                    ? AssetHealthGrade.Healthy
                    : score >= 70
                        ? AssetHealthGrade.Attention
                        : AssetHealthGrade.Degraded;
                return new AssetHealthScorePoint
                {
                    Sequence = index + 1,
                    AssetId = "MOTOR-1",
                    HealthScore = score,
                    PreviousHealthScore = index == 0 ? score : score + 4,
                    ScoreDelta = index == 0 ? 0 : -4,
                    Grade = grade,
                    PreviousGrade = index == 0 ? grade : AssetHealthGrade.Attention,
                    GradeChanged = index > 0,
                    Direction = AssetHealthTrendDirection.Deteriorating,
                    FusionRiskScore = 0.10 + index * 0.10,
                    FusionStatus = FusedHealthStatus.Normal,
                    IndependentSourceCount = 2,
                    CalculatedAtUtc = start.AddTicks(interval.Ticks * index),
                    RecordedAtUtc = start.AddTicks(interval.Ticks * index),
                    Summary = "test"
                };
            })
            .ToArray();
    }

    private static AssetFailureForecastModelManifest CreateManifest()
    {
        var artifact = new byte[] { 1, 2, 3 };
        return new AssetFailureForecastModelManifest
        {
            Version = "rul-v1",
            ArtifactFile = "model.onnx",
            ArtifactSha256 = Convert.ToHexString(SHA256.HashData(artifact)),
            CreatedUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            Source = "approved-offline-training",
            ApprovedBy = "reliability-engineer",
            ApprovedAtUtc = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
            TrainingDatasetVersion = "site-degradation-v1",
            TrainingAssetCount = 100,
            FailureEventCount = 20,
            CensoredRecordCount = 80,
            ValidationAuc = 0.80,
            ValidationBrierScore = 0.15,
            ValidationRulMaeHours = 24,
            ValidationIntervalCoverage = 0.85,
            FeatureNames = AssetFailureForecastFeatureSchema.Names.ToArray(),
            Means = Enumerable.Repeat(0d, AssetFailureForecastFeatureSchema.Names.Length).ToArray(),
            StandardDeviations = Enumerable.Repeat(1d, AssetFailureForecastFeatureSchema.Names.Length).ToArray(),
            InputShape = new[] { 1, AssetFailureForecastFeatureSchema.Names.Length },
            OutputShape = new[] { 1, 6 },
            MaximumRulHours = 1_000
        };
    }
}
