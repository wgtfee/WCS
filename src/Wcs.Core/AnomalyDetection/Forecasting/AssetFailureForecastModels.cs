namespace Wcs.Core.AnomalyDetection.Forecasting;

using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection.HealthScoring;

public enum AssetFailureForecastAvailability
{
    Disabled = 0,
    ModelUnavailable = 1,
    InsufficientData = 2,
    Ready = 3,
    Failed = 4
}

public enum AssetFailureForecastOutcomeKind
{
    ObservedFailure = 0,
    PreventiveMaintenance = 1,
    CensoredNoFailure = 2,
    InvalidPrediction = 3
}

public sealed class AssetFailureForecastOptions
{
    public bool Enabled { get; set; }
    public string ModelDirectory { get; set; } = "data/failure-forecast-models";
    public int EvaluationIntervalSeconds { get; set; } = 300;
    public int MinimumHistoryPoints { get; set; } = 48;
    public int MinimumHistorySpanHours { get; set; } = 24;
    public int MaximumHistoryPoints { get; set; } = 2_000;
    public int MaximumAssetsPerEvaluation { get; set; } = 1_000;
    public int MaximumForecastsQueryCount { get; set; } = 1_000;
    public int ForecastRetentionHours { get; set; } = 8_760;
    public int MaintenanceIntervalSeconds { get; set; } = 3_600;
    public int MaintenanceBatchSize { get; set; } = 2_000;
    public int MaximumModelArtifactMegabytes { get; set; } = 256;
    public int MinimumTrainingAssets { get; set; } = 30;
    public int MinimumFailureEvents { get; set; } = 10;
    public double MinimumValidationAuc { get; set; } = 0.65;
    public double MaximumValidationBrierScore { get; set; } = 0.30;
    public double MinimumPredictionIntervalCoverage { get; set; } = 0.70;
}

/// <summary>
/// Approved metadata for one local failure-probability and RUL model.
/// It contains no remote URL, key, license payload or training data.
/// </summary>
public sealed class AssetFailureForecastModelManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = string.Empty;
    public string AdapterId { get; set; } = "microsoft.onnxruntime.cpu.forecast.v1";
    public string ArtifactFile { get; set; } = string.Empty;
    public string ArtifactSha256 { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public string TrainingDatasetVersion { get; set; } = string.Empty;
    public int TrainingAssetCount { get; set; }
    public int FailureEventCount { get; set; }
    public int CensoredRecordCount { get; set; }
    public double ValidationAuc { get; set; }
    public double ValidationBrierScore { get; set; }
    public double ValidationRulMaeHours { get; set; }
    public double ValidationIntervalCoverage { get; set; }
    public string[] FeatureNames { get; set; } = Array.Empty<string>();
    public double[] Means { get; set; } = Array.Empty<double>();
    public double[] StandardDeviations { get; set; } = Array.Empty<double>();
    public string InputName { get; set; } = "features";
    public string OutputName { get; set; } = "forecast";
    public int[] InputShape { get; set; } = Array.Empty<int>();
    public int[] OutputShape { get; set; } = new[] { 1, 6 };
    public double MaximumRulHours { get; set; } = 17_520;
    public string? Description { get; set; }
}

public sealed record AssetFailureForecastModelArtifact
{
    public required AssetFailureForecastModelManifest Manifest { get; init; }
    public required ReadOnlyMemory<byte> Content { get; init; }
}

public sealed record AssetFailureForecastFeatureVector
{
    public required string AssetId { get; init; }
    public required DateTime WindowStartUtc { get; init; }
    public required DateTime WindowEndUtc { get; init; }
    public required IReadOnlyList<string> FeatureNames { get; init; }
    public required IReadOnlyList<double> Values { get; init; }
    public required int SampleCount { get; init; }
    public required double HistorySpanHours { get; init; }
}

public sealed record AssetFailureForecastOutput
{
    public required double FailureProbability24Hours { get; init; }
    public required double FailureProbability72Hours { get; init; }
    public required double FailureProbability168Hours { get; init; }
    public required double RulLowerHours { get; init; }
    public required double RulMedianHours { get; init; }
    public required double RulUpperHours { get; init; }
}

public sealed record AssetFailureForecastPrediction
{
    public required string ForecastId { get; init; }
    public required string AssetId { get; init; }
    public required string ModelVersion { get; init; }
    public required string ManifestHash { get; init; }
    public required DateTime WindowStartUtc { get; init; }
    public required DateTime WindowEndUtc { get; init; }
    public required DateTime ForecastedAtUtc { get; init; }
    public required int SampleCount { get; init; }
    public required double HistorySpanHours { get; init; }
    public required double FailureProbability24Hours { get; init; }
    public required double FailureProbability72Hours { get; init; }
    public required double FailureProbability168Hours { get; init; }
    public required double RulLowerHours { get; init; }
    public required double RulMedianHours { get; init; }
    public required double RulUpperHours { get; init; }
    public required double CurrentHealthScore { get; init; }
    public required AssetHealthGrade CurrentGrade { get; init; }
    public required string Explanation { get; init; }
}

public sealed record AssetFailureForecastAttempt
{
    public required string AssetId { get; init; }
    public required AssetFailureForecastAvailability Availability { get; init; }
    public AssetFailureForecastPrediction? Prediction { get; init; }
    public string? Reason { get; init; }
}

public sealed record AssetFailureForecastOutcome
{
    public required string OutcomeId { get; init; }
    public required string ForecastId { get; init; }
    public required AssetFailureForecastOutcomeKind Kind { get; init; }
    public required DateTime ObservedAtUtc { get; init; }
    public required string RecordedBy { get; init; }
    public required string Note { get; init; }
}

public sealed record AssetFailureForecastMetrics
{
    public required int ForecastCount { get; init; }
    public required int OutcomeCount { get; init; }
    public required int ObservedFailureCount { get; init; }
    public required double? MeanAbsoluteRulErrorHours { get; init; }
    public required double? PredictionIntervalCoverage { get; init; }
    public required double? BrierScore24Hours { get; init; }
}

public sealed record AssetFailureForecastStatus
{
    public required bool Enabled { get; init; }
    public required AssetFailureForecastAvailability Availability { get; init; }
    public string? ActiveModelVersion { get; init; }
    public string? ManifestHash { get; init; }
    public string? ArtifactSha256 { get; init; }
    public required long EvaluationAttempts { get; init; }
    public required long ForecastsCreated { get; init; }
    public required long InsufficientData { get; init; }
    public required long Failures { get; init; }
    public DateTime? LoadedUtc { get; init; }
    public string? LastError { get; init; }
}

public interface IAssetFailureForecastRuntime : IDisposable
{
    AssetFailureForecastModelManifest Manifest { get; }
    AssetFailureForecastOutput Predict(AssetFailureForecastFeatureVector vector);
}

public interface IAssetFailureForecastModelStore
{
    Task<AssetFailureForecastModelArtifact?> LoadActiveAsync(CancellationToken cancellationToken = default);
    Task<AssetFailureForecastModelArtifact?> LoadVersionAsync(string version, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetFailureForecastModelManifest>> ListAsync(CancellationToken cancellationToken = default);
    Task ActivateAsync(string version, CancellationToken cancellationToken = default);
}

public interface IAssetFailureForecastStore
{
    Task EnsureModelVersionAsync(AssetFailureForecastModelManifest manifest, string manifestHash, CancellationToken cancellationToken = default);
    Task<bool> SaveForecastAsync(AssetFailureForecastPrediction forecast, CancellationToken cancellationToken = default);
    Task<AssetFailureForecastPrediction?> GetLatestAsync(string assetId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetFailureForecastPrediction>> QueryAsync(string? assetId, int maximumCount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetFailureForecastOutcome>> GetOutcomesAsync(string forecastId, CancellationToken cancellationToken = default);
    Task<bool> AppendOutcomeAsync(AssetFailureForecastOutcome outcome, CancellationToken cancellationToken = default);
    Task<AssetFailureForecastMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
    Task MaintainAsync(DateTime utcNow, CancellationToken cancellationToken = default);
}

public interface IAssetFailureForecastService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<AssetFailureForecastAttempt> EvaluateAssetAsync(string assetId, DateTime utcNow, CancellationToken cancellationToken = default);
    Task<int> EvaluateAllAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    Task<AssetFailureForecastPrediction?> GetLatestAsync(string assetId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetFailureForecastPrediction>> QueryAsync(string? assetId, int maximumCount, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetFailureForecastModelManifest>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task ActivateModelAsync(string version, CancellationToken cancellationToken = default);
    Task<bool> AppendOutcomeAsync(AssetFailureForecastOutcome outcome, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssetFailureForecastOutcome>> GetOutcomesAsync(string forecastId, CancellationToken cancellationToken = default);
    Task<AssetFailureForecastMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
    AssetFailureForecastStatus GetStatus();
}

public static class AssetFailureForecastIdentity
{
    public static string CreateForecastId(
        string assetId,
        string modelVersion,
        DateTime windowStartUtc,
        DateTime windowEndUtc)
    {
        var canonical = string.Join('|',
            assetId.Trim(),
            modelVersion.Trim(),
            windowStartUtc.ToUniversalTime().ToString("O"),
            windowEndUtc.ToUniversalTime().ToString("O"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CreateOutcomeId(
        string forecastId,
        AssetFailureForecastOutcomeKind kind,
        DateTime observedAtUtc,
        string recordedBy)
    {
        var canonical = string.Join('|',
            forecastId.Trim(),
            (int)kind,
            observedAtUtc.ToUniversalTime().ToString("O"),
            recordedBy.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
