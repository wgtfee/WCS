namespace Wcs.Simulator.Tests;

using Wcs.IndustrialIntelligence.Governance;
using Wcs.Infrastructure.IndustrialIntelligence;

public sealed class BoundedAutomationSqlEvidenceTests
{
    private const string GitSha40 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Fact]
    public async Task SqlStore_SchemaAndAppendGet_RoundTrip()
    {
        var store = CreateStore();
        var record = CreateRecord(NewId("roundtrip"), DateTimeOffset.UtcNow);

        await store.AppendAsync(record);
        var loaded = await store.GetAsync(record.EvaluationId);

        Assert.NotNull(loaded);
        Assert.Equal(record.EvaluationId, loaded!.EvaluationId);
        Assert.Equal(record.DecisionHash, loaded.DecisionHash);
        Assert.Equal(record.PolicyHash, loaded.PolicyHash);
        Assert.Equal(record.SourceEvidenceHash, loaded.SourceEvidenceHash);
        Assert.False(loaded.ProductionEnablementAllowed);
        Assert.Equal("software-side ready only", loaded.Claim);
    }

    [Fact]
    public async Task SqlStore_SameImmutableRecord_IsIdempotent()
    {
        var store = CreateStore();
        var record = CreateRecord(NewId("idempotent"), DateTimeOffset.UtcNow);

        await store.AppendAsync(record);
        await store.AppendAsync(record);

        var loaded = await store.GetAsync(record.EvaluationId);
        Assert.NotNull(loaded);
        Assert.Equal(record.DecisionHash, loaded!.DecisionHash);
    }

    [Fact]
    public async Task SqlStore_ConflictingDuplicateEvaluationId_IsRejected()
    {
        var store = CreateStore();
        var record = CreateRecord(NewId("conflict"), DateTimeOffset.UtcNow);
        await store.AppendAsync(record);

        var conflicting = record with { DecisionHash = Hashing.Sha256("conflicting-decision") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(conflicting));
    }

    [Fact]
    public async Task SqlStore_List_IsBoundedAndNewestFirst()
    {
        var store = CreateStore();
        var prefix = NewId("list");
        var older = CreateRecord($"{prefix}-a", DateTimeOffset.UtcNow.AddMinutes(-2));
        var middle = CreateRecord($"{prefix}-b", DateTimeOffset.UtcNow.AddMinutes(-1));
        var newest = CreateRecord($"{prefix}-c", DateTimeOffset.UtcNow);

        await store.AppendAsync(older);
        await store.AppendAsync(middle);
        await store.AppendAsync(newest);

        var values = await store.ListAsync(500);
        var ours = values.Where(x => x.EvaluationId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, ours.Length);
        Assert.Equal(newest.EvaluationId, ours[0].EvaluationId);
        Assert.Equal(middle.EvaluationId, ours[1].EvaluationId);
        Assert.Equal(older.EvaluationId, ours[2].EvaluationId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(501));
    }

    [Fact]
    public async Task SqlStore_RoundTrip_PreservesPermanentSoftwareOnlyBoundary()
    {
        var store = CreateStore();
        var request = ValidRequest(AutomationLevel.L3, realEvidence: true);
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        Assert.True(decision.SoftwareSideReady);
        Assert.False(decision.ProductionEnablementAllowed);

        var record = BoundedAutomationReadinessEvidenceRecord.Create(
            NewId("l3"), DateTimeOffset.UtcNow, request, decision);
        await store.AppendAsync(record);
        var loaded = await store.GetAsync(record.EvaluationId);

        Assert.NotNull(loaded);
        Assert.Equal(AutomationLevel.L3, loaded!.RequestedLevel);
        Assert.True(loaded.SoftwareSideReady);
        Assert.False(loaded.ProductionEnablementAllowed);
        Assert.Equal(BoundedAutomationReadinessGovernance.SoftwareOnlyClaim, loaded.Claim);
    }

    [Fact]
    public async Task SqlStore_InvalidProductionEnabledEvidence_IsRejectedBeforeInsert()
    {
        var store = CreateStore();
        var valid = CreateRecord(NewId("invalid"), DateTimeOffset.UtcNow);
        var invalid = valid with { ProductionEnablementAllowed = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(invalid));
        Assert.Null(await store.GetAsync(invalid.EvaluationId));
    }

    private static IBoundedAutomationReadinessEvidenceStore CreateStore()
    {
        var connectionString = Environment.GetEnvironmentVariable("WCS_P6_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("WCS_P6_SQL_CONNECTION is required for P6 SQL integration tests.");
        var factory = new BoundedAutomationReadinessPersistenceFactory(connectionString);
        factory.EnsureSchema();
        return factory.CreateStore();
    }

    private static BoundedAutomationReadinessEvidenceRecord CreateRecord(string evaluationId, DateTimeOffset evaluatedAtUtc)
    {
        var request = ValidRequest(AutomationLevel.L1, realEvidence: false);
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        return BoundedAutomationReadinessEvidenceRecord.Create(evaluationId, evaluatedAtUtc, request, decision);
    }

    private static BoundedAutomationReadinessRequest ValidRequest(AutomationLevel level, bool realEvidence) => new(
        "IndustrialIntelligence",
        new AutomationPolicy(true, level, "sql-v1", Hashing.Sha256($"sql-policy:{level}")),
        new ExecutionAllowance(true, ExecutionAllowanceKind.SoftwareSimulation),
        new RateLimit(true, 60),
        new BudgetLimit(true, 100m),
        new MaintenanceWindow(true, TimeSpan.FromHours(1), TimeSpan.FromHours(2)),
        new ApprovalRequirement(true, 2, true),
        new CircuitBreaker(true, 3, TimeSpan.FromMinutes(1)),
        new KillSwitch(true, true),
        new RollbackPolicy(true, "sql-v0", TimeSpan.FromMinutes(5)),
        new BoundedAutomationEvidence(
            true,
            realEvidence,
            realEvidence,
            realEvidence,
            realEvidence,
            GitSha40,
            Hashing.Sha256("sql-source-evidence")),
        Array.Empty<PermanentAutomationProhibition>());

    private static string NewId(string prefix) => $"p6-{prefix}-{Guid.NewGuid():N}";
}
