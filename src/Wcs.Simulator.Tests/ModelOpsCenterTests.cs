namespace Wcs.Simulator.Tests;

using System.Text.Json;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.ModelOps;

public sealed class ModelOpsCenterTests
{
    private const long MiB = 1_048_576;
    private const string FeatureSchemaJson = "{\"features\":[\"health.latest\"]}";
    private static readonly DateTimeOffset ApprovedAt = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ManifestHash_IsDeterministic()
    {
        var first = CreateManifest("v1");
        var second = CreateManifest("v1");

        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.Equal(first.ManifestHash, ModelManifestHash.Compute(first));
        Assert.True(Hashing.IsSha256(first.ManifestHash));
    }

    [Fact]
    public void ManifestHash_ChangesWhenFeatureSchemaChanges()
    {
        var first = CreateManifest("v1");
        var changed = first with { FeatureSchemaId = "schema-v2", ManifestHash = new string('0', 64) };
        changed = changed with { ManifestHash = ModelManifestHash.Compute(changed) };

        Assert.NotEqual(first.ManifestHash, changed.ManifestHash);
    }

    [Fact]
    public async Task Registry_RejectsSameVersionWithDifferentManifestHash()
    {
        var registry = new InMemoryModelRegistry();
        var first = CreateVersion(CreateManifest("v1"));
        await registry.RegisterAsync(first, CancellationToken.None);

        var changedManifest = CreateManifest("v1") with
        {
            TrainingDatasetVersion = "dataset-v2",
            ManifestHash = new string('0', 64)
        };
        changedManifest = changedManifest with { ManifestHash = ModelManifestHash.Compute(changedManifest) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RegisterAsync(CreateVersion(changedManifest), CancellationToken.None));
    }

    [Fact]
    public async Task Registry_IdenticalRegistration_IsIdempotent()
    {
        var registry = new InMemoryModelRegistry();
        var version = CreateVersion(CreateManifest("v1"));

        await registry.RegisterAsync(version, CancellationToken.None);
        await registry.RegisterAsync(version, CancellationToken.None);

        var values = await registry.ListAsync("asset-health", CancellationToken.None);
        Assert.Single(values);
    }

    [Fact]
    public async Task PackageValidator_RejectsPathTraversal()
    {
        var package = await CreatePackageAsync();
        try
        {
            var manifest = await ReadManifestAsync(package);
            manifest = manifest with { ArtifactFile = "../outside.onnx", ManifestHash = new string('0', 64) };
            manifest = manifest with { ManifestHash = ModelManifestHash.Compute(manifest) };
            await WriteManifestAsync(package, manifest);

            var result = await new LocalModelPackageValidator(4 * MiB)
                .ValidateAsync(package, CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, x => x.Contains("traverse", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task PackageValidator_RejectsOversizedPackage()
    {
        var package = await CreatePackageAsync();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(package, "padding.bin"), new byte[MiB]);
            var result = await new LocalModelPackageValidator(MiB)
                .ValidateAsync(package, CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, x => x.Contains("MaximumModelPackageBytes", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task PackageValidator_RejectsArtifactHashMismatch()
    {
        var package = await CreatePackageAsync();
        try
        {
            var manifest = await ReadManifestAsync(package);
            manifest = manifest with { ArtifactSha256 = Hashing.Sha256("wrong-artifact"), ManifestHash = new string('0', 64) };
            manifest = manifest with { ManifestHash = ModelManifestHash.Compute(manifest) };
            await WriteManifestAsync(package, manifest);

            var result = await new LocalModelPackageValidator(4 * MiB)
                .ValidateAsync(package, CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, x => x.Contains("ArtifactSha256", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task PackageValidator_RejectsFeatureSchemaHashMismatch()
    {
        var package = await CreatePackageAsync();
        try
        {
            var manifest = await ReadManifestAsync(package);
            manifest = manifest with { FeatureSchemaHash = Hashing.Sha256("wrong-schema"), ManifestHash = new string('0', 64) };
            manifest = manifest with { ManifestHash = ModelManifestHash.Compute(manifest) };
            await WriteManifestAsync(package, manifest);

            var result = await new LocalModelPackageValidator(4 * MiB)
                .ValidateAsync(package, CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, x => x.Contains("FeatureSchemaHash", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task PackageValidator_RejectsInvalidInputShape()
    {
        var package = await CreatePackageAsync();
        try
        {
            var manifest = await ReadManifestAsync(package);
            manifest = manifest with { InputShape = [1, 0, 14], ManifestHash = new string('0', 64) };
            manifest = manifest with { ManifestHash = ModelManifestHash.Compute(manifest) };
            await WriteManifestAsync(package, manifest);

            var result = await new LocalModelPackageValidator(4 * MiB)
                .ValidateAsync(package, CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, x => x.Contains("InputShape", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task PackageValidator_RejectsNonOnnxArtifact()
    {
        var package = await CreatePackageAsync();
        try
        {
            File.Copy(Path.Combine(package, "model.onnx"), Path.Combine(package, "model.bin"));
            var manifest = await ReadManifestAsync(package);
            manifest = manifest with { ArtifactFile = "model.bin", ManifestHash = new string('0', 64) };
            manifest = manifest with { ManifestHash = ModelManifestHash.Compute(manifest) };
            await WriteManifestAsync(package, manifest);

            var result = await new LocalModelPackageValidator(4 * MiB)
                .ValidateAsync(package, CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, x => x.Contains(".onnx", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task PackageValidator_AcceptsValidGovernedPackage()
    {
        var package = await CreatePackageAsync();
        try
        {
            var result = await new LocalModelPackageValidator(4 * MiB)
                .ValidateAsync(package, CancellationToken.None);

            Assert.True(result.IsValid, string.Join(" | ", result.Errors));
            Assert.NotNull(result.Manifest);
            Assert.Equal("asset-health", result.Manifest!.ModelId);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task Deployment_RejectsUnapprovedVersionForShadow()
    {
        var registry = new InMemoryModelRegistry();
        await registry.RegisterAsync(
            CreateVersion(CreateManifest("v1", approved: false)),
            CancellationToken.None);
        var manager = new InMemoryModelDeploymentManager(registry);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PromoteToShadowAsync(Request("v1"), CancellationToken.None));
    }

    [Fact]
    public async Task Deployment_AllowsApprovedCandidateToEnterShadow()
    {
        var registry = new InMemoryModelRegistry();
        await registry.RegisterAsync(CreateVersion(CreateManifest("v1")), CancellationToken.None);
        var manager = new InMemoryModelDeploymentManager(registry);

        await manager.PromoteToShadowAsync(Request("v1"), CancellationToken.None);

        var deployment = Assert.Single(manager.Snapshot());
        Assert.Equal(AiModelLifecycleStatus.Shadow, deployment.Status);
    }

    [Fact]
    public async Task Deployment_ChampionRequiresShadowFirst()
    {
        var registry = new InMemoryModelRegistry();
        await registry.RegisterAsync(CreateVersion(CreateManifest("v1")), CancellationToken.None);
        var manager = new InMemoryModelDeploymentManager(registry);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PromoteToChampionAsync(Request("v1"), CancellationToken.None));
    }

    [Fact]
    public async Task Deployment_NewChampionMakesOldChampionFallbackAndKeepsSingleChampion()
    {
        var registry = new InMemoryModelRegistry();
        await registry.RegisterAsync(CreateVersion(CreateManifest("v1")), CancellationToken.None);
        await registry.RegisterAsync(CreateVersion(CreateManifest("v2")), CancellationToken.None);
        var manager = new InMemoryModelDeploymentManager(registry);

        await manager.PromoteToShadowAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToShadowAsync(Request("v2"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request("v2"), CancellationToken.None);

        var snapshot = manager.Snapshot();
        Assert.Single(snapshot.Where(x => x.Status == AiModelLifecycleStatus.Champion));
        Assert.Equal("v2", snapshot.Single(x => x.Status == AiModelLifecycleStatus.Champion).ModelVersion);
        Assert.Equal("v1", snapshot.Single(x => x.Status == AiModelLifecycleStatus.Fallback).ModelVersion);
    }

    [Fact]
    public async Task Deployment_RollbackSwapsChampionAndFallback()
    {
        var registry = new InMemoryModelRegistry();
        await registry.RegisterAsync(CreateVersion(CreateManifest("v1")), CancellationToken.None);
        await registry.RegisterAsync(CreateVersion(CreateManifest("v2")), CancellationToken.None);
        var manager = new InMemoryModelDeploymentManager(registry);

        await manager.PromoteToShadowAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToShadowAsync(Request("v2"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request("v2"), CancellationToken.None);
        await manager.RollbackAsync(
            new ModelRollbackRequest("asset-health", "RGV", "default", "operator-a", "rollback test", "corr-rb"),
            CancellationToken.None);

        var snapshot = manager.Snapshot();
        Assert.Equal("v1", snapshot.Single(x => x.Status == AiModelLifecycleStatus.Champion).ModelVersion);
        Assert.Equal("v2", snapshot.Single(x => x.Status == AiModelLifecycleStatus.Fallback).ModelVersion);
    }

    private static AiModelPackageManifest CreateManifest(string version, bool approved = true)
    {
        var manifest = new AiModelPackageManifest(
            ModelId: "asset-health",
            ModelVersion: version,
            ModelType: "ONNX",
            ArtifactFile: "model.onnx",
            ArtifactSha256: Hashing.Sha256("model-payload"),
            ManifestHash: new string('0', 64),
            FeatureSchemaId: "schema-v1",
            FeatureSchemaHash: Hashing.Sha256(FeatureSchemaJson),
            TrainingDatasetVersion: "dataset-v1",
            TrainingDatasetHash: Hashing.Sha256("dataset-evidence"),
            TrainingAssetCount: 12,
            FailureEventCount: 4,
            ValidationMetrics: new Dictionary<string, double>
            {
                ["auc"] = 0.91,
                ["brier"] = 0.08
            },
            RuntimeLimits: new AiModelRuntimeLimits(200, 256 * MiB),
            ApprovedBy: approved ? "approver-a" : string.Empty,
            ApprovedAtUtc: approved ? ApprovedAt : null,
            FallbackVersion: null,
            InputShape: [1, 14],
            OutputShape: [1, 6]);

        return manifest with { ManifestHash = ModelManifestHash.Compute(manifest) };
    }

    private static AiModelVersion CreateVersion(AiModelPackageManifest manifest) =>
        new(
            manifest,
            AiModelLifecycleStatus.Candidate,
            ApprovedAt,
            "operator-a",
            $"corr-{manifest.ModelVersion}");

    private static ModelDeploymentRequest Request(string version) =>
        new(
            "asset-health",
            version,
            "RGV",
            "default",
            "operator-a",
            "P1 test promotion",
            $"corr-{version}");

    private static async Task<string> CreatePackageAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wcs-idi-p1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "model.onnx"), "model-payload");
        await File.WriteAllTextAsync(Path.Combine(root, "feature-schema.json"), FeatureSchemaJson);
        await File.WriteAllTextAsync(Path.Combine(root, "normalization.json"), "{\"kind\":\"none\"}");
        await File.WriteAllTextAsync(Path.Combine(root, "validation-evidence.json"), "{\"approved\":true}");
        await WriteManifestAsync(root, CreateManifest("v1"));
        return root;
    }

    private static async Task<AiModelPackageManifest> ReadManifestAsync(string package)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(package, "manifest.json"));
        return JsonSerializer.Deserialize<AiModelPackageManifest>(json)!;
    }

    private static Task WriteManifestAsync(string package, AiModelPackageManifest manifest) =>
        File.WriteAllTextAsync(
            Path.Combine(package, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
}
