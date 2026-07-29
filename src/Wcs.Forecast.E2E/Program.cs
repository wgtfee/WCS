using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Infrastructure.AnomalyDetection.Forecasting;

if (args.Length != 2)
    throw new ArgumentException("Usage: Wcs.Forecast.E2E <model.onnx> <evidence.json>");

var modelPath = Path.GetFullPath(args[0]);
var evidencePath = Path.GetFullPath(args[1]);
if (!File.Exists(modelPath)) throw new FileNotFoundException("Forecast ONNX model was not found.", modelPath);
Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);

var options = new AssetFailureForecastOptions
{
    Enabled = true,
    ModelDirectory = Path.Combine(Path.GetDirectoryName(evidencePath)!, "model-store"),
    MinimumTrainingAssets = 30,
    MinimumFailureEvents = 10,
    MinimumValidationAuc = 0.65,
    MaximumValidationBrierScore = 0.30,
    MinimumPredictionIntervalCoverage = 0.70
};
var modelBytes = await File.ReadAllBytesAsync(modelPath);
var manifest = new AssetFailureForecastModelManifest
{
    Version = "forecast-e2e-v1",
    ArtifactFile = "forecast-e2e-v1.onnx",
    ArtifactSha256 = Convert.ToHexString(SHA256.HashData(modelBytes)),
    CreatedUtc = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
    Source = "github-actions-generated-offline",
    ApprovedBy = "ci-reliability-reviewer",
    ApprovedAtUtc = new DateTime(2026, 7, 28, 0, 1, 0, DateTimeKind.Utc),
    TrainingDatasetVersion = "ci-degradation-dataset-v1",
    TrainingAssetCount = 100,
    FailureEventCount = 20,
    CensoredRecordCount = 80,
    ValidationAuc = 0.82,
    ValidationBrierScore = 0.14,
    ValidationRulMaeHours = 18,
    ValidationIntervalCoverage = 0.84,
    FeatureNames = AssetFailureForecastFeatureSchema.Names.ToArray(),
    Means = Enumerable.Repeat(0d, AssetFailureForecastFeatureSchema.Names.Length).ToArray(),
    StandardDeviations = Enumerable.Repeat(1d, AssetFailureForecastFeatureSchema.Names.Length).ToArray(),
    InputName = "features",
    OutputName = "forecast",
    InputShape = new[] { 1, AssetFailureForecastFeatureSchema.Names.Length },
    OutputShape = new[] { 1, 6 },
    MaximumRulHours = 1_000,
    Description = "Deterministic constant-output model generated inside isolated CI."
};
AssetFailureForecastManifestValidator.Validate(manifest, options);

Directory.CreateDirectory(options.ModelDirectory);
var storedModelPath = Path.Combine(options.ModelDirectory, manifest.ArtifactFile);
await File.WriteAllBytesAsync(storedModelPath, modelBytes);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
await File.WriteAllTextAsync(
    Path.Combine(options.ModelDirectory, $"manifest-{manifest.Version}.json"),
    JsonSerializer.Serialize(manifest, jsonOptions));

var store = new FileAssetFailureForecastModelStore(options);
await store.ActivateAsync(manifest.Version);
var loaded = await store.LoadActiveAsync()
    ?? throw new InvalidOperationException("Active forecast model was not restored from the local store.");
var vector = new AssetFailureForecastFeatureVector
{
    AssetId = "MOTOR-E2E",
    WindowStartUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
    WindowEndUtc = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc),
    FeatureNames = AssetFailureForecastFeatureSchema.Names,
    Values = Enumerable.Repeat(0d, AssetFailureForecastFeatureSchema.Names.Length).ToArray(),
    SampleCount = 48,
    HistorySpanHours = 48
};

AssetFailureForecastOutput prediction;
AssetFailureForecastOutput reloadedPrediction;
double samplesPerSecond;
long memoryGrowthBytes;
using (var runtime = new OnnxAssetFailureForecastRuntime(loaded, options))
{
    prediction = runtime.Predict(vector);
    Require(Math.Abs(prediction.FailureProbability24Hours - 0.10) < 0.0001, "Unexpected 24h probability.");
    Require(Math.Abs(prediction.FailureProbability72Hours - 0.30) < 0.0001, "Unexpected 72h probability.");
    Require(Math.Abs(prediction.FailureProbability168Hours - 0.60) < 0.0001, "Unexpected 168h probability.");
    Require(Math.Abs(prediction.RulLowerHours - 40) < 0.0001, "Unexpected RUL lower bound.");
    Require(Math.Abs(prediction.RulMedianHours - 72) < 0.0001, "Unexpected RUL median.");
    Require(Math.Abs(prediction.RulUpperHours - 120) < 0.0001, "Unexpected RUL upper bound.");

    var featureOrderRejected = false;
    try
    {
        runtime.Predict(vector with { FeatureNames = vector.FeatureNames.Reverse().ToArray() });
    }
    catch (InvalidOperationException)
    {
        featureOrderRejected = true;
    }
    Require(featureOrderRejected, "Forecast feature-order mismatch must be rejected.");

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var before = Process.GetCurrentProcess().WorkingSet64;
    const int iterations = 20_000;
    var stopwatch = Stopwatch.StartNew();
    for (var index = 0; index < iterations; index++)
        _ = runtime.Predict(vector);
    stopwatch.Stop();
    samplesPerSecond = iterations / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var after = Process.GetCurrentProcess().WorkingSet64;
    memoryGrowthBytes = Math.Max(0, after - before);
    Require(samplesPerSecond > 1_000, $"Forecast ONNX throughput is too low: {samplesPerSecond:F2}/s.");
    Require(memoryGrowthBytes <= 256L * 1024 * 1024,
        $"Forecast ONNX RSS growth exceeded 256 MB: {memoryGrowthBytes / 1024d / 1024d:F2} MB.");
}

var badHash = Clone(manifest);
badHash.ArtifactSha256 = new string('0', 64);
var hashRejected = false;
try
{
    using var ignored = new OnnxAssetFailureForecastRuntime(
        new AssetFailureForecastModelArtifact { Manifest = badHash, Content = modelBytes },
        options);
}
catch (InvalidOperationException)
{
    hashRejected = true;
}
Require(hashRejected, "Forecast artifact SHA mismatch must be rejected.");

var badShape = Clone(manifest);
badShape.InputShape = new[] { 1, AssetFailureForecastFeatureSchema.Names.Length + 1 };
var shapeRejected = false;
try
{
    using var ignored = new OnnxAssetFailureForecastRuntime(
        new AssetFailureForecastModelArtifact { Manifest = badShape, Content = modelBytes },
        options);
}
catch (InvalidOperationException)
{
    shapeRejected = true;
}
Require(shapeRejected, "Forecast input shape mismatch must be rejected.");

var weakEvidence = Clone(manifest);
weakEvidence.FailureEventCount = 0;
var weakEvidenceRejected = false;
try
{
    using var ignored = new OnnxAssetFailureForecastRuntime(
        new AssetFailureForecastModelArtifact { Manifest = weakEvidence, Content = modelBytes },
        options);
}
catch (InvalidOperationException)
{
    weakEvidenceRejected = true;
}
Require(weakEvidenceRejected, "A forecast model without real failure evidence must be rejected.");

var restartedStore = new FileAssetFailureForecastModelStore(options);
var restored = await restartedStore.LoadActiveAsync()
    ?? throw new InvalidOperationException("Active forecast model was not recovered after store recreation.");
using (var restartedRuntime = new OnnxAssetFailureForecastRuntime(restored, options))
    reloadedPrediction = restartedRuntime.Predict(vector);
Require(Math.Abs(reloadedPrediction.RulMedianHours - prediction.RulMedianHours) < 1e-9,
    "Restarted forecast runtime did not produce deterministic output.");
var versions = await restartedStore.ListAsync();
Require(versions.Count == 1, $"Expected one forecast model version, actual={versions.Count}.");
var manifestHash = AssetFailureForecastManifestValidator.ComputeManifestHash(restored.Manifest);
Require(manifestHash == AssetFailureForecastManifestValidator.ComputeManifestHash(manifest),
    "Forecast manifest hash is not deterministic after recovery.");

var evidence = new
{
    runtime = "Microsoft.ML.OnnxRuntime CPU",
    adapter = OnnxAssetFailureForecastRuntime.RuntimeAdapterId,
    offlineLocalArtifact = true,
    remoteDownloadUsed = false,
    manifest.Version,
    manifest.ArtifactSha256,
    manifestHash,
    featureCount = manifest.FeatureNames.Length,
    outputCount = 6,
    prediction,
    reloadedPrediction,
    samplesPerSecond,
    memoryGrowthBytes,
    hashRejected,
    shapeRejected,
    featureOrderRejected = true,
    weakEvidenceRejected,
    versionCount = versions.Count,
    restartRecovery = true,
    controlWrites = 0
};
await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, jsonOptions));
Console.WriteLine(JsonSerializer.Serialize(evidence, jsonOptions));
return;

static AssetFailureForecastModelManifest Clone(AssetFailureForecastModelManifest source) => new()
{
    SchemaVersion = source.SchemaVersion,
    Version = source.Version,
    AdapterId = source.AdapterId,
    ArtifactFile = source.ArtifactFile,
    ArtifactSha256 = source.ArtifactSha256,
    CreatedUtc = source.CreatedUtc,
    Source = source.Source,
    ApprovedBy = source.ApprovedBy,
    ApprovedAtUtc = source.ApprovedAtUtc,
    TrainingDatasetVersion = source.TrainingDatasetVersion,
    TrainingAssetCount = source.TrainingAssetCount,
    FailureEventCount = source.FailureEventCount,
    CensoredRecordCount = source.CensoredRecordCount,
    ValidationAuc = source.ValidationAuc,
    ValidationBrierScore = source.ValidationBrierScore,
    ValidationRulMaeHours = source.ValidationRulMaeHours,
    ValidationIntervalCoverage = source.ValidationIntervalCoverage,
    FeatureNames = source.FeatureNames.ToArray(),
    Means = source.Means.ToArray(),
    StandardDeviations = source.StandardDeviations.ToArray(),
    InputName = source.InputName,
    OutputName = source.OutputName,
    InputShape = source.InputShape.ToArray(),
    OutputShape = source.OutputShape.ToArray(),
    MaximumRulHours = source.MaximumRulHours,
    Description = source.Description
};

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
