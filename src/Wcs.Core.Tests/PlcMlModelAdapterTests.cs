namespace Wcs.Core.Tests;

using System.Security.Cryptography;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.AnomalyDetection.MachineLearning.Adapters;

public sealed class PlcMlModelAdapterTests
{
    [Fact]
    public void Valid_manifest_has_deterministic_hash()
    {
        var profile = CreateProfile();
        var manifest = CreateManifest(profile);

        PlcMlModelManifestValidator.Validate(profile, manifest);
        PlcMlFeatureSchema.ValidateManifest(profile, manifest);
        var first = PlcMlModelManifestValidator.ComputeManifestHash(manifest);
        var second = PlcMlModelManifestValidator.ComputeManifestHash(manifest);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Feature_schema_is_deterministic_and_exact()
    {
        var profile = CreateProfile();
        var expected = new[]
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

        Assert.Equal(expected, PlcMlFeatureSchema.BuildExpectedFeatureNames(profile));
        var manifest = CreateManifest(profile);
        PlcMlFeatureSchema.ValidateManifest(profile, manifest);

        manifest.FeatureNames = manifest.FeatureNames.Reverse().ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            PlcMlFeatureSchema.ValidateManifest(profile, manifest));
    }

    [Fact]
    public void Manifest_rejects_duplicate_features_and_path_traversal()
    {
        var profile = CreateProfile();
        var duplicate = CreateManifest(profile);
        duplicate.FeatureNames = new[] { "Current.mean", "Current.mean" };
        duplicate.Means = new[] { 0d, 0d };
        duplicate.StandardDeviations = new[] { 1d, 1d };
        duplicate.InputShape = new[] { 1, 2 };
        Assert.Throws<InvalidOperationException>(() => PlcMlModelManifestValidator.Validate(profile, duplicate));

        var traversal = CreateManifest(profile);
        traversal.ArtifactFile = "../model.onnx";
        Assert.Throws<InvalidOperationException>(() => PlcMlModelManifestValidator.Validate(profile, traversal));
    }

    [Fact]
    public void Artifact_hash_mismatch_is_rejected()
    {
        var profile = CreateProfile();
        var manifest = CreateManifest(profile);
        var content = new byte[] { 1, 2, 3, 4 };
        manifest.ArtifactSha256 = Convert.ToHexString(SHA256.HashData(new byte[] { 9, 9, 9 }));

        Assert.Throws<InvalidOperationException>(() =>
            PlcMlModelManifestValidator.VerifyArtifactHash(manifest, content));
    }

    [Fact]
    public void Score_transforms_are_bounded_and_deterministic()
    {
        Assert.Equal(0.5, PlcMlScoreTransformer.Apply(0, PlcMlScoreTransform.Sigmoid), 12);
        Assert.Equal(0.75, PlcMlScoreTransformer.Apply(0.25, PlcMlScoreTransform.OneMinus), 12);
        Assert.Equal(0.25, PlcMlScoreTransformer.Apply(0.25, PlcMlScoreTransform.Identity), 12);
        Assert.Throws<InvalidOperationException>(() =>
            PlcMlScoreTransformer.Apply(1.2, PlcMlScoreTransform.Identity));
    }

    [Fact]
    public void Registry_rejects_duplicate_kinds_and_unknown_kind()
    {
        Assert.Throws<InvalidOperationException>(() => new PlcMlModelAdapterRegistry(new IPlcMlModelAdapter[]
        {
            new FakeAdapter(PlcMlModelAdapterKind.Onnx, "a"),
            new FakeAdapter(PlcMlModelAdapterKind.Onnx, "b")
        }));

        var registry = new PlcMlModelAdapterRegistry(new[]
        {
            new FakeAdapter(PlcMlModelAdapterKind.Onnx, "onnx")
        });
        Assert.Equal("onnx", registry.Resolve(PlcMlModelAdapterKind.Onnx).AdapterId);
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve(PlcMlModelAdapterKind.IsolationForest));
    }

    private static PlcMlProfile CreateProfile() => new()
    {
        ProfileId = "CV-MOTOR",
        Signals = new List<PlcMlSignalDefinition>
        {
            new() { Name = "Current", Pattern = "*.Current", Kind = PlcMlSignalKind.Numeric }
        }
    };

    private static PlcMlModelManifest CreateManifest(PlcMlProfile profile)
    {
        var artifact = new byte[] { 1, 2, 3 };
        var features = PlcMlFeatureSchema.BuildExpectedFeatureNames(profile);
        return new PlcMlModelManifest
        {
            ProfileId = profile.ProfileId,
            Version = "onnx-v1",
            AdapterKind = PlcMlModelAdapterKind.Onnx,
            AdapterId = "microsoft.onnxruntime.cpu.v1",
            ArtifactFile = "model-onnx-v1.onnx",
            ArtifactSha256 = Convert.ToHexString(SHA256.HashData(artifact)),
            CreatedUtc = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
            Source = "approved-offline-training",
            ApprovedBy = "maintenance-engineer",
            ApprovedAtUtc = new DateTime(2026, 7, 28, 1, 0, 0, DateTimeKind.Utc),
            FeatureNames = features,
            Means = Enumerable.Repeat(0d, features.Length).ToArray(),
            StandardDeviations = Enumerable.Repeat(1d, features.Length).ToArray(),
            InputName = "features",
            OutputName = "score",
            InputShape = new[] { 1, features.Length },
            ScoreTransform = PlcMlScoreTransform.Sigmoid,
            DecisionThreshold = 0.8,
            CalibrationMeanScore = 0.2,
            CalibrationP95Score = 0.5
        };
    }

    private sealed class FakeAdapter : IPlcMlModelAdapter
    {
        public FakeAdapter(PlcMlModelAdapterKind kind, string adapterId)
        {
            Kind = kind;
            AdapterId = adapterId;
        }

        public PlcMlModelAdapterKind Kind { get; }
        public string AdapterId { get; }

        public Task<IPlcMlModelRuntime> LoadAsync(
            PlcMlProfile profile,
            PlcMlModelArtifact artifact,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
