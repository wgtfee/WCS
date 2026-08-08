namespace Wcs.Simulator.Tests;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.IndustrialIntelligence.Governance;

public sealed class BoundedAutomationEvidenceContractTests
{
    private const string GitSha40 = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void EvidenceHash_IsDeterministic()
    {
        var request = ValidRequest();
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        Assert.Equal(
            BoundedAutomationReadinessEvidenceHash.Compute(request, decision),
            BoundedAutomationReadinessEvidenceHash.Compute(request, decision));
    }

    [Fact]
    public void EvidenceHash_ChangesWhenPolicyChanges()
    {
        var request = ValidRequest();
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        var changed = request with
        {
            AutomationPolicy = request.AutomationPolicy with
            {
                PolicyVersion = "p6-v2",
                PolicyHash = Hashing.Sha256("p6-policy-v2")
            }
        };
        var changedDecision = BoundedAutomationReadinessEvaluator.Evaluate(changed);
        Assert.NotEqual(
            BoundedAutomationReadinessEvidenceHash.Compute(request, decision),
            BoundedAutomationReadinessEvidenceHash.Compute(changed, changedDecision));
    }

    [Fact]
    public void EvidenceRecord_CreateProducesGovernedHashes()
    {
        var request = ValidRequest();
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        var record = BoundedAutomationReadinessEvidenceRecord.Create("eval-1", DateTimeOffset.UtcNow, request, decision);
        Assert.True(Hashing.IsSha256(record.DecisionHash));
        Assert.True(Hashing.IsSha256(record.PolicyHash));
        Assert.True(Hashing.IsSha256(record.SourceEvidenceHash));
        Assert.False(record.ProductionEnablementAllowed);
        Assert.Equal("software-side ready only", record.Claim);
    }

    [Fact]
    public void EvidenceRecord_RejectsInvalidEvaluationId()
    {
        var request = ValidRequest();
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        Assert.Throws<ArgumentException>(() =>
            BoundedAutomationReadinessEvidenceRecord.Create("", DateTimeOffset.UtcNow, request, decision));
    }

    [Fact]
    public void EvidenceRecord_RejectsProductionEnablementDecision()
    {
        var request = ValidRequest();
        var decision = new BoundedAutomationReadinessDecision(
            true, true, AutomationLevel.L1, "software-side ready only", Array.Empty<string>());
        Assert.Throws<InvalidOperationException>(() =>
            BoundedAutomationReadinessEvidenceRecord.Create("eval-prod", DateTimeOffset.UtcNow, request, decision));
    }

    [Fact]
    public async Task InMemoryEvidenceStore_IsIdempotentForSameImmutableRecord()
    {
        var store = new InMemoryBoundedAutomationReadinessEvidenceStore();
        var record = CreateRecord("eval-idempotent");
        await store.AppendAsync(record);
        await store.AppendAsync(record);
        Assert.Single(await store.ListAsync(10));
        Assert.Equal(record, await store.GetAsync(record.EvaluationId));
    }

    [Fact]
    public async Task InMemoryEvidenceStore_RejectsConflictingDuplicateEvaluationId()
    {
        var store = new InMemoryBoundedAutomationReadinessEvidenceStore();
        var first = CreateRecord("eval-conflict");
        await store.AppendAsync(first);
        var conflicting = first with { DecisionHash = Hashing.Sha256("different-decision") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(conflicting));
    }

    [Fact]
    public async Task InMemoryEvidenceStore_RejectsUnboundedList()
    {
        var store = new InMemoryBoundedAutomationReadinessEvidenceStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(501));
    }

    [Fact]
    public void Controller_ExposesOnlyFourHttpGetActions()
    {
        var actions = typeof(BoundedAutomationReadinessController)
            .GetMethods()
            .Where(method => method.DeclaringType == typeof(BoundedAutomationReadinessController))
            .Where(method => method.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any())
            .ToArray();
        Assert.Equal(4, actions.Length);
        Assert.All(actions, method =>
        {
            var attribute = Assert.Single(method.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>());
            Assert.Equal(["GET"], attribute.HttpMethods);
        });
    }

    [Fact]
    public void ControllerStatus_IsReadOnlyAndSoftwareSideOnly()
    {
        var controller = CreateController("IndustrialIntelligence");
        var result = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("software-side ready only", json, StringComparison.Ordinal);
        Assert.Contains("\"productionEnablementAllowed\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"controlWriteAllowed\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"executionApiExposed\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerPermanentProhibitions_ContainsAllElevenValues()
    {
        var controller = CreateController("IndustrialIntelligence");
        var result = Assert.IsType<OkObjectResult>(controller.GetPermanentProhibitions());
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        foreach (var value in Enum.GetNames<PermanentAutomationProhibition>())
            Assert.Contains(value, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerProductionEnvironment_IsFailClosed()
    {
        var controller = CreateController("Production");
        Assert.IsType<NotFoundResult>(controller.GetStatus());
        Assert.IsType<NotFoundResult>(controller.GetPermanentProhibitions());
    }

    private static BoundedAutomationReadinessEvidenceRecord CreateRecord(string evaluationId)
    {
        var request = ValidRequest();
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        return BoundedAutomationReadinessEvidenceRecord.Create(evaluationId, DateTimeOffset.UtcNow, request, decision);
    }

    private static BoundedAutomationReadinessRequest ValidRequest() => new(
        "IndustrialIntelligence",
        new AutomationPolicy(true, AutomationLevel.L1, "p6-v1", Hashing.Sha256("p6-policy-v1")),
        new ExecutionAllowance(true, ExecutionAllowanceKind.SoftwareSimulation),
        new RateLimit(true, 60),
        new BudgetLimit(true, 100m),
        new MaintenanceWindow(true, TimeSpan.FromHours(1), TimeSpan.FromHours(2)),
        new ApprovalRequirement(true, 2, true),
        new CircuitBreaker(true, 3, TimeSpan.FromMinutes(1)),
        new KillSwitch(true, true),
        new RollbackPolicy(true, "p6-v0", TimeSpan.FromMinutes(5)),
        new BoundedAutomationEvidence(true, false, false, false, false, GitSha40, Hashing.Sha256("source-evidence")),
        Array.Empty<PermanentAutomationProhibition>());

    private static BoundedAutomationReadinessController CreateController(string environment)
    {
        var values = new Dictionary<string, string?>
        {
            ["IndustrialIntelligence:Enabled"] = "true",
            ["IndustrialIntelligence:Mode"] = "ReadOnly",
            ["IndustrialIntelligence:AllowedEnvironments:0"] = "IndustrialIntelligence",
            ["IndustrialIntelligence:MaximumAutomationLevel"] = "L1"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new BoundedAutomationReadinessController(
            new TestHostEnvironment { EnvironmentName = environment }, configuration);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Wcs.Host.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
