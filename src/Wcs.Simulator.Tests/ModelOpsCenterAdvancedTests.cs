namespace Wcs.Simulator.Tests;

using Microsoft.Data.SqlClient;
using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;
using Wcs.ModelOps;

public sealed class ModelOpsCenterAdvancedTests
{
    private static readonly DateTimeOffset ApprovedAt = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PersistentDeployment_ThirdChampionRetiresOldFallback()
    {
        var registry = await RegistryAsync("v1", "v2", "v3");
        var store = new InMemoryModelDeploymentStore();
        var audit = new InMemoryModelOpsAuditJournal();
        var manager = new PersistentModelDeploymentManager(registry, store, audit);

        foreach (var version in new[] { "v1", "v2", "v3" })
        {
            await manager.PromoteToShadowAsync(Request(version), CancellationToken.None);
            await manager.PromoteToChampionAsync(Request(version), CancellationToken.None);
        }

        var snapshot = await store.ListScopeAsync("asset-health", "RGV", "default", CancellationToken.None);
        Assert.Single(snapshot.Where(x => x.Status == AiModelLifecycleStatus.Champion));
        Assert.Single(snapshot.Where(x => x.Status == AiModelLifecycleStatus.Fallback));
        Assert.Equal("v3", snapshot.Single(x => x.Status == AiModelLifecycleStatus.Champion).ModelVersion);
        Assert.Equal("v2", snapshot.Single(x => x.Status == AiModelLifecycleStatus.Fallback).ModelVersion);
        Assert.Equal(AiModelLifecycleStatus.Retired, snapshot.Single(x => x.ModelVersion == "v1").Status);
    }

    [Fact]
    public async Task PersistentDeployment_QuarantineChampionFailsClosedWithoutAutoFallbackPromotion()
    {
        var registry = await RegistryAsync("v1", "v2");
        var store = new InMemoryModelDeploymentStore();
        var audit = new InMemoryModelOpsAuditJournal();
        var manager = new PersistentModelDeploymentManager(registry, store, audit);
        await manager.PromoteToShadowAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToShadowAsync(Request("v2"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request("v2"), CancellationToken.None);

        await manager.QuarantineAsync(
            new ModelQuarantineRequest("asset-health", "v2", "RGV", "default", "operator-a", "drift", "corr-q"),
            CancellationToken.None);

        var snapshot = await store.ListScopeAsync("asset-health", "RGV", "default", CancellationToken.None);
        Assert.Empty(snapshot.Where(x => x.Status == AiModelLifecycleStatus.Champion));
        Assert.Equal("v1", snapshot.Single(x => x.Status == AiModelLifecycleStatus.Fallback).ModelVersion);
        Assert.Equal(AiModelLifecycleStatus.Quarantined, snapshot.Single(x => x.ModelVersion == "v2").Status);
        var entries = await audit.ListAsync("asset-health", 20, CancellationToken.None);
        Assert.Contains(entries, x => x.Action == "QuarantineChampionFailClosed");
    }

    [Fact]
    public async Task Recovery_RejectsDuplicateChampionInsteadOfGuessing()
    {
        var registry = await RegistryAsync("v1", "v2");
        var store = new UnsafeDeploymentStore([
            Deployment("v1", AiModelLifecycleStatus.Champion),
            Deployment("v2", AiModelLifecycleStatus.Champion)
        ]);
        var recovery = new ModelDeploymentRecoveryService(registry, store);

        var report = await recovery.ValidateAsync(CancellationToken.None);

        Assert.False(report.IsHealthy);
        Assert.Contains(report.Errors, x => x.Contains("Champion", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ModelDeploymentInvariantException>(() => recovery.EnsureHealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Recovery_RejectsDeploymentReferencingMissingRegistryVersion()
    {
        var registry = new InMemoryModelRegistry();
        var store = new UnsafeDeploymentStore([Deployment("missing", AiModelLifecycleStatus.Shadow)]);
        var recovery = new ModelDeploymentRecoveryService(registry, store);

        var report = await recovery.ValidateAsync(CancellationToken.None);

        Assert.False(report.IsHealthy);
        Assert.Contains(report.Errors, x => x.Contains("missing registry version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuditJournal_IsAppendOnlyAndRejectsDuplicateAuditId()
    {
        var journal = new InMemoryModelOpsAuditJournal();
        var entry = Audit("audit-1");
        await journal.AppendAsync(entry, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(entry, CancellationToken.None));
        Assert.Single(await journal.ListAsync("asset-health", 10, CancellationToken.None));
    }

    [Fact]
    public async Task ShadowRuntime_ExecutesShadowOnlyAndWritesEvidenceWithZeroControl()
    {
        var registry = await RegistryAsync("v1", "v2");
        var store = new InMemoryModelDeploymentStore();
        var manager = new PersistentModelDeploymentManager(registry, store, new InMemoryModelOpsAuditJournal());
        await manager.PromoteToShadowAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request("v1"), CancellationToken.None);
        await manager.PromoteToShadowAsync(Request("v2"), CancellationToken.None);
        var journal = new InMemoryShadowInferenceJournal();
        var runtime = new GovernedShadowRuntime(registry, store, new FakeInferenceRunner(), journal);

        var results = await runtime.ExecuteAsync(
            "asset-health",
            "RGV",
            "default",
            Input("schema-v1"),
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("v2", result.ModelVersion);
        var evidence = Assert.Single(await journal.ListAsync("asset-health", 10, CancellationToken.None));
        Assert.False(evidence.ControlWriteAllowed);
        Assert.Equal("v2", evidence.ModelVersion);
    }

    [Fact]
    public async Task ShadowRuntime_FeatureSchemaMismatchFailsClosed()
    {
        var registry = await RegistryAsync("v1");
        var store = new InMemoryModelDeploymentStore();
        var manager = new PersistentModelDeploymentManager(registry, store, new InMemoryModelOpsAuditJournal());
        await manager.PromoteToShadowAsync(Request("v1"), CancellationToken.None);
        var runtime = new GovernedShadowRuntime(registry, store, new FakeInferenceRunner(), new InMemoryShadowInferenceJournal());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            "asset-health", "RGV", "default", Input("wrong-schema"), CancellationToken.None));
    }

    [Fact]
    public async Task ChampionChallenger_EvaluationPersistsEvidenceButNeverAutoPromotes()
    {
        var store = new InMemoryModelEvaluationStore();
        var evaluator = new ChampionChallengerEvaluator(store);
        var observations = new[]
        {
            new ChampionChallengerObservation("A", 10, 11, 12),
            new ChampionChallengerObservation("B", 20, 20.5, 21)
        };

        var result = await evaluator.EvaluateAsync(
            "asset-health", "v2", "dataset-v1", Hashing.Sha256("dataset"), observations, "corr-eval", CancellationToken.None);

        Assert.True(result.ChallengerIsBetter);
        Assert.False(result.AutoPromotionAllowed);
        Assert.True(Hashing.IsSha256(result.Evaluation.EvidenceSha256));
        Assert.Single(await store.ListAsync("asset-health", 10, CancellationToken.None));
    }

    [Fact]
    public async Task DriftMonitor_BelowThresholdDoesNotCreateEvent()
    {
        var store = new InMemoryModelDriftStore();
        var monitor = new ModelDriftMonitor(store);

        var result = await monitor.ObserveAsync(
            "asset-health", "v1", "psi", 0.05, 0.10, "corr-drift", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(await store.ListAsync("asset-health", 10, CancellationToken.None));
    }

    [Fact]
    public async Task DriftMonitor_AboveThresholdCreatesEvidenceOnlyEvent()
    {
        var store = new InMemoryModelDriftStore();
        var monitor = new ModelDriftMonitor(store);

        var result = await monitor.ObserveAsync(
            "asset-health", "v1", "psi", 0.15, 0.10, "corr-drift", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(Hashing.IsSha256(result!.EvidenceSha256));
        Assert.Single(await store.ListAsync("asset-health", 10, CancellationToken.None));
    }

    [Fact]
    public async Task SqlRegistry_PersistsAcrossRegistryInstances()
    {
        var modelId = UniqueModelId("sql-reg");
        var first = SqlFactory();
        var version = Version(modelId, "v1");
        await first.CreateRegistry().RegisterAsync(version, CancellationToken.None);

        var second = SqlFactory();
        var restored = await second.CreateRegistry().GetAsync(modelId, "v1", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(version.Manifest.ManifestHash, restored!.Manifest.ManifestHash);
        Assert.Equal(version.Manifest.FeatureSchemaHash, restored.Manifest.FeatureSchemaHash);
    }

    [Fact]
    public async Task SqlRegistry_RejectsSameVersionWithDifferentManifestHash()
    {
        var modelId = UniqueModelId("sql-conflict");
        var factory = SqlFactory();
        var registry = factory.CreateRegistry();
        await registry.RegisterAsync(Version(modelId, "v1"), CancellationToken.None);
        var changed = Manifest(modelId, "v1") with
        {
            TrainingDatasetVersion = "dataset-v2",
            ManifestHash = new string('0', 64)
        };
        changed = changed with { ManifestHash = ModelManifestHash.Compute(changed) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterAsync(
            new AiModelVersion(changed, AiModelLifecycleStatus.Candidate, ApprovedAt, "operator-a", "corr-conflict"),
            CancellationToken.None));
    }

    [Fact]
    public async Task SqlDeployment_RestartRecoveryRestoresChampionAndFallback()
    {
        var modelId = UniqueModelId("sql-recovery");
        var factory = SqlFactory();
        var registry = factory.CreateRegistry();
        await registry.RegisterAsync(Version(modelId, "v1"), CancellationToken.None);
        await registry.RegisterAsync(Version(modelId, "v2"), CancellationToken.None);
        var manager = new PersistentModelDeploymentManager(registry, factory.CreateDeploymentStore(), factory.CreateAuditJournal());
        await manager.PromoteToShadowAsync(Request(modelId, "v1"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request(modelId, "v1"), CancellationToken.None);
        await manager.PromoteToShadowAsync(Request(modelId, "v2"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request(modelId, "v2"), CancellationToken.None);

        var restartedFactory = SqlFactory();
        var report = await restartedFactory.CreateRecoveryService().ValidateAsync(CancellationToken.None);
        var restored = await restartedFactory.CreateDeploymentStore().ListScopeAsync(modelId, "RGV", "default", CancellationToken.None);

        Assert.True(report.IsHealthy, string.Join(" | ", report.Errors));
        Assert.Equal("v2", restored.Single(x => x.Status == AiModelLifecycleStatus.Champion).ModelVersion);
        Assert.Equal("v1", restored.Single(x => x.Status == AiModelLifecycleStatus.Fallback).ModelVersion);
    }

    [Fact]
    public async Task SqlDeployment_ThreePromotionsKeepExactlyOneChampionAndOneFallback()
    {
        var modelId = UniqueModelId("sql-singletons");
        var factory = SqlFactory();
        var registry = factory.CreateRegistry();
        foreach (var version in new[] { "v1", "v2", "v3" })
            await registry.RegisterAsync(Version(modelId, version), CancellationToken.None);
        var manager = new PersistentModelDeploymentManager(registry, factory.CreateDeploymentStore(), factory.CreateAuditJournal());
        foreach (var version in new[] { "v1", "v2", "v3" })
        {
            await manager.PromoteToShadowAsync(Request(modelId, version), CancellationToken.None);
            await manager.PromoteToChampionAsync(Request(modelId, version), CancellationToken.None);
        }

        var restored = await factory.CreateDeploymentStore().ListScopeAsync(modelId, "RGV", "default", CancellationToken.None);
        Assert.Single(restored.Where(x => x.Status == AiModelLifecycleStatus.Champion));
        Assert.Single(restored.Where(x => x.Status == AiModelLifecycleStatus.Fallback));
        Assert.Equal("v3", restored.Single(x => x.Status == AiModelLifecycleStatus.Champion).ModelVersion);
        Assert.Equal("v2", restored.Single(x => x.Status == AiModelLifecycleStatus.Fallback).ModelVersion);
    }

    [Fact]
    public async Task SqlAuditJournal_DuplicateAuditIdIsRejectedByAppendOnlyConstraint()
    {
        var modelId = UniqueModelId("sql-audit");
        var factory = SqlFactory();
        var audit = factory.CreateAuditJournal();
        var entry = Audit("audit-" + Guid.NewGuid().ToString("N"), modelId);
        await audit.AppendAsync(entry, CancellationToken.None);

        await Assert.ThrowsAsync<SqlException>(() => audit.AppendAsync(entry, CancellationToken.None));
        Assert.Contains(await audit.ListAsync(modelId, 10, CancellationToken.None), x => x.AuditId == entry.AuditId);
    }

    [Fact]
    public async Task SqlQuarantine_PersistsFailClosedStateAndAuditAcrossInstances()
    {
        var modelId = UniqueModelId("sql-quarantine");
        var factory = SqlFactory();
        var registry = factory.CreateRegistry();
        await registry.RegisterAsync(Version(modelId, "v1"), CancellationToken.None);
        var manager = new PersistentModelDeploymentManager(registry, factory.CreateDeploymentStore(), factory.CreateAuditJournal());
        await manager.PromoteToShadowAsync(Request(modelId, "v1"), CancellationToken.None);
        await manager.PromoteToChampionAsync(Request(modelId, "v1"), CancellationToken.None);
        await manager.QuarantineAsync(
            new ModelQuarantineRequest(modelId, "v1", "RGV", "default", "operator-a", "bad evidence", "corr-q"),
            CancellationToken.None);

        var restarted = SqlFactory();
        var deployments = await restarted.CreateDeploymentStore().ListScopeAsync(modelId, "RGV", "default", CancellationToken.None);
        var audits = await restarted.CreateAuditJournal().ListAsync(modelId, 20, CancellationToken.None);

        Assert.Equal(AiModelLifecycleStatus.Quarantined, Assert.Single(deployments).Status);
        Assert.Contains(audits, x => x.Action == "QuarantineChampionFailClosed");
    }

    private static async Task<InMemoryModelRegistry> RegistryAsync(params string[] versions)
    {
        var registry = new InMemoryModelRegistry();
        foreach (var version in versions)
            await registry.RegisterAsync(Version("asset-health", version), CancellationToken.None);
        return registry;
    }

    private static AiModelVersion Version(string modelId, string version) =>
        new(Manifest(modelId, version), AiModelLifecycleStatus.Candidate, ApprovedAt, "operator-a", $"corr-{version}");

    private static AiModelPackageManifest Manifest(string modelId, string version)
    {
        var manifest = new AiModelPackageManifest(
            modelId,
            version,
            "ONNX",
            "model.onnx",
            Hashing.Sha256("model-payload-" + version),
            new string('0', 64),
            "schema-v1",
            Hashing.Sha256("{\"features\":[\"health.latest\"]}"),
            "dataset-v1",
            Hashing.Sha256("dataset-evidence"),
            12,
            4,
            new Dictionary<string, double> { ["auc"] = 0.91, ["brier"] = 0.08 },
            new AiModelRuntimeLimits(2000, 256 * 1_048_576L),
            "approver-a",
            ApprovedAt,
            null,
            [1, 14],
            [1, 6]);
        return manifest with { ManifestHash = ModelManifestHash.Compute(manifest) };
    }

    private static ModelDeploymentRequest Request(string version) => Request("asset-health", version);

    private static ModelDeploymentRequest Request(string modelId, string version) =>
        new(modelId, version, "RGV", "default", "operator-a", "P1 governed promotion", $"corr-{version}");

    private static AiModelDeployment Deployment(string version, AiModelLifecycleStatus status) =>
        new("asset-health", version, "RGV", "default", status, DateTimeOffset.UtcNow, "operator-a", "test", "corr-test");

    private static ModelInferenceInput Input(string schema) =>
        new("RGV-01", schema, new Dictionary<string, double> { ["health.latest"] = 0.8 }, DateTimeOffset.UtcNow, "corr-shadow");

    private static AiModelAuditEntry Audit(string id, string modelId = "asset-health") =>
        new(id, "Test", modelId, "v1", "operator-a", "test", DateTimeOffset.UtcNow, "corr-audit", Hashing.Sha256(id));

    private static string UniqueModelId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static ModelOpsPersistenceFactory SqlFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("IDI_P1_SQL_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "IDI_P1_SQL_CONNECTION must be configured by the P1 workflow.");
        var factory = new ModelOpsPersistenceFactory(connectionString!);
        factory.EnsureSchema();
        return factory;
    }

    private sealed class FakeInferenceRunner : IModelInferenceRunner
    {
        public Task<ModelInferenceResult> RunAsync(AiModelVersion version, ModelInferenceInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ModelInferenceResult(
                version.ModelId,
                version.Version,
                [0.25, 0.75],
                1,
                Hashing.Sha256($"{version.ModelId}|{version.Version}|{input.AssetId}|{input.ObservedAtUtc:O}"),
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class UnsafeDeploymentStore : IModelDeploymentStore
    {
        private readonly IReadOnlyList<AiModelDeployment> _items;

        public UnsafeDeploymentStore(IReadOnlyList<AiModelDeployment> items)
        {
            _items = items;
        }

        public Task<IReadOnlyList<AiModelDeployment>> ListScopeAsync(string modelId, string assetType, string profile, CancellationToken ct) =>
            Task.FromResult(_items);

        public Task<IReadOnlyList<AiModelDeployment>> ListAllAsync(CancellationToken ct) => Task.FromResult(_items);

        public Task ApplyAsync(IReadOnlyList<AiModelDeployment> deployments, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
