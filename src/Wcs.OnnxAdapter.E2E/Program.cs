using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.AnomalyDetection.MachineLearning.Adapters;
using Wcs.Infrastructure.AnomalyDetection.MachineLearning.Adapters;

if (args.Length != 2)
    throw new ArgumentException("Usage: Wcs.OnnxAdapter.E2E <model.onnx> <evidence.json>");

var modelPath = Path.GetFullPath(args[0]);
var evidencePath = Path.GetFullPath(args[1]);
if (!File.Exists(modelPath)) throw new FileNotFoundException("ONNX model was not found.", modelPath);
Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);

var featureNames = new[]
{
    "Current.mean",
    "Current.stddev",
    "Current.min",
    "Current.max",
    "Current.last",
    "Current.slope",
    "Current.range",
    "Current.samplesPerSecond"
};
var profile = new PlcMlProfile
{
    ProfileId = "ONNX-E2E",
    Enabled = true,
    Signals = new List<PlcMlSignalDefinition>
    {
        new() { Name = "Current", Pattern = "*.Current", Kind = PlcMlSignalKind.Numeric }
    }
};
var modelBytes = await File.ReadAllBytesAsync(modelPath);
var manifest = new PlcMlModelManifest
{
    ProfileId = profile.ProfileId,
    Version = "onnx-e2e-v1",
    AdapterKind = PlcMlModelAdapterKind.Onnx,
    AdapterId = OnnxPlcMlModelAdapter.RuntimeAdapterId,
    ArtifactFile = "model-onnx-e2e-v1.onnx",
    ArtifactSha256 = Convert.ToHexString(SHA256.HashData(modelBytes)),
    CreatedUtc = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
    Source = "github-actions-generated",
    ApprovedBy = "ci-model-reviewer",
    ApprovedAtUtc = new DateTime(2026, 7, 28, 0, 1, 0, DateTimeKind.Utc),
    FeatureNames = featureNames,
    Means = Enumerable.Repeat(0d, featureNames.Length).ToArray(),
    StandardDeviations = Enumerable.Repeat(1d, featureNames.Length).ToArray(),
    InputName = "features",
    OutputName = "score",
    InputShape = new[] { 1, featureNames.Length },
    ScoreTransform = PlcMlScoreTransform.Identity,
    DecisionThreshold = 0.8,
    CalibrationMeanScore = 0.2,
    CalibrationP95Score = 0.5,
    Description = "Deterministic ReduceMean + Sigmoid model generated inside isolated CI."
};
PlcMlModelManifestValidator.Validate(profile, manifest);

var workDirectory = Path.Combine(Path.GetDirectoryName(evidencePath)!, "model-store");
var profileDirectory = Path.Combine(workDirectory, "external", profile.ProfileId);
Directory.CreateDirectory(profileDirectory);
var storedModelPath = Path.Combine(profileDirectory, manifest.ArtifactFile);
await File.WriteAllBytesAsync(storedModelPath, modelBytes);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var versionManifestPath = Path.Combine(profileDirectory, $"manifest-{manifest.Version}.json");
await File.WriteAllTextAsync(versionManifestPath, JsonSerializer.Serialize(manifest, jsonOptions));

var options = new PlcMlAnomalyOptions { ModelDirectory = workDirectory };
var store = new FilePlcMlExternalModelStore(options);
await store.ActivateAsync(profile.ProfileId, manifest.Version);
var loaded = await store.LoadActiveAsync(profile.ProfileId)
    ?? throw new InvalidOperationException("Active ONNX artifact was not restored from the local store.");

var registry = new PlcMlModelAdapterRegistry(new IPlcMlModelAdapter[]
{
    new IsolationForestPlcMlModelAdapter(),
    new OnnxPlcMlModelAdapter()
});
var adapter = registry.Resolve(PlcMlModelAdapterKind.Onnx);

var normalVector = Vector(featureNames, Enumerable.Repeat(-2d, featureNames.Length).ToArray());
var anomalyVector = Vector(featureNames, Enumerable.Repeat(2d, featureNames.Length).ToArray());

double normalScore;
double anomalyScore;
double reloadedScore;
double samplesPerSecond;
long memoryGrowthBytes;
using (var runtime = await adapter.LoadAsync(profile, loaded))
{
    var normal = runtime.Predict(normalVector);
    var anomaly = runtime.Predict(anomalyVector);
    normalScore = normal.Score;
    anomalyScore = anomaly.Score;
    Require(normal.DetectorName == "ONNXRuntime", "DetectorName must identify ONNX Runtime.");
    Require(!normal.IsAnomaly, $"Normal vector was classified as anomalous: {normal.Score}.");
    Require(anomaly.IsAnomaly, $"Anomaly vector was not classified as anomalous: {anomaly.Score}.");
    Require(Math.Abs(normal.Score - 0.1192029) < 0.001, $"Unexpected normal score: {normal.Score}.");
    Require(Math.Abs(anomaly.Score - 0.8807971) < 0.001, $"Unexpected anomaly score: {anomaly.Score}.");

    var featureOrderRejected = false;
    try
    {
        runtime.Predict(Vector(featureNames.Reverse().ToArray(), anomalyVector.Values));
    }
    catch (InvalidOperationException)
    {
        featureOrderRejected = true;
    }
    Require(featureOrderRejected, "A feature-order mismatch must be rejected before inference.");

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var before = Process.GetCurrentProcess().WorkingSet64;
    const int iterations = 20_000;
    var stopwatch = Stopwatch.StartNew();
    for (var index = 0; index < iterations; index++)
        _ = runtime.Predict((index & 1) == 0 ? normalVector : anomalyVector);
    stopwatch.Stop();
    samplesPerSecond = iterations / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var after = Process.GetCurrentProcess().WorkingSet64;
    memoryGrowthBytes = Math.Max(0, after - before);
    Require(samplesPerSecond > 1_000, $"ONNX adapter throughput is too low: {samplesPerSecond:F2}/s.");
    Require(memoryGrowthBytes <= 256L * 1024 * 1024,
        $"ONNX adapter RSS growth exceeded 256 MB: {memoryGrowthBytes / 1024d / 1024d:F2} MB.");
}

var badHash = Clone(manifest);
badHash.ArtifactSha256 = new string('0', 64);
var hashRejected = false;
try
{
    using var ignored = await adapter.LoadAsync(profile, new PlcMlModelArtifact
    {
        Manifest = badHash,
        Content = modelBytes
    });
}
catch (InvalidOperationException)
{
    hashRejected = true;
}
Require(hashRejected, "Tampered ONNX bytes or SHA-256 metadata must be rejected.");

var badShape = Clone(manifest);
badShape.InputShape = new[] { 1, featureNames.Length + 1 };
var shapeRejected = false;
try
{
    using var ignored = await adapter.LoadAsync(profile, new PlcMlModelArtifact
    {
        Manifest = badShape,
        Content = modelBytes
    });
}
catch (InvalidOperationException)
{
    shapeRejected = true;
}
Require(shapeRejected, "Manifest input shape mismatch must be rejected.");

var restartedStore = new FilePlcMlExternalModelStore(new PlcMlAnomalyOptions { ModelDirectory = workDirectory });
var restored = await restartedStore.LoadActiveAsync(profile.ProfileId)
    ?? throw new InvalidOperationException("Active ONNX artifact was not recovered after store recreation.");
using (var reloadedRuntime = await new OnnxPlcMlModelAdapter().LoadAsync(profile, restored))
    reloadedScore = reloadedRuntime.Predict(anomalyVector).Score;
Require(Math.Abs(reloadedScore - anomalyScore) < 1e-9, "Reloaded ONNX runtime did not produce a deterministic score.");

var manifests = await restartedStore.ListAsync(profile.ProfileId);
Require(manifests.Count == 1, $"Expected one versioned manifest, actual={manifests.Count}.");
var activeManifestHash = PlcMlModelManifestValidator.ComputeManifestHash(restored.Manifest);
Require(activeManifestHash == PlcMlModelManifestValidator.ComputeManifestHash(manifest),
    "Recovered active manifest hash is not deterministic.");

var evidence = new
{
    adapter = adapter.AdapterId,
    runtime = "Microsoft.ML.OnnxRuntime CPU",
    offlineLocalArtifact = true,
    remoteDownloadUsed = false,
    manifest.ProfileId,
    manifest.Version,
    manifest.ArtifactSha256,
    manifestHash = activeManifestHash,
    featureCount = featureNames.Length,
    normalScore,
    anomalyScore,
    reloadedScore,
    samplesPerSecond,
    memoryGrowthBytes,
    hashRejected,
    shapeRejected,
    featureOrderRejected = true,
    versionCount = manifests.Count,
    restartRecovery = true,
    controlWrites = 0
};
await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, jsonOptions));
Console.WriteLine(JsonSerializer.Serialize(evidence, jsonOptions));
return;

static PlcFeatureVector Vector(string[] featureNames, double[] values) => new()
{
    ProfileId = "ONNX-E2E",
    PlcName = "SIM-PLC",
    DeviceId = "SIM-MOTOR-01",
    WindowStartUtc = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
    WindowEndUtc = new DateTime(2026, 7, 28, 0, 0, 10, DateTimeKind.Utc),
    FeatureNames = featureNames,
    Values = values,
    SourceSampleCount = 10,
    ContextKey = "E2E"
};

static PlcMlModelManifest Clone(PlcMlModelManifest source) => new()
{
    SchemaVersion = source.SchemaVersion,
    ProfileId = source.ProfileId,
    Version = source.Version,
    AdapterKind = source.AdapterKind,
    AdapterId = source.AdapterId,
    ArtifactFile = source.ArtifactFile,
    ArtifactSha256 = source.ArtifactSha256,
    CreatedUtc = source.CreatedUtc,
    Source = source.Source,
    ApprovedBy = source.ApprovedBy,
    ApprovedAtUtc = source.ApprovedAtUtc,
    FeatureNames = source.FeatureNames.ToArray(),
    Means = source.Means.ToArray(),
    StandardDeviations = source.StandardDeviations.ToArray(),
    InputName = source.InputName,
    OutputName = source.OutputName,
    InputShape = source.InputShape.ToArray(),
    ScoreTransform = source.ScoreTransform,
    DecisionThreshold = source.DecisionThreshold,
    CalibrationMeanScore = source.CalibrationMeanScore,
    CalibrationP95Score = source.CalibrationP95Score,
    Description = source.Description
};

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
