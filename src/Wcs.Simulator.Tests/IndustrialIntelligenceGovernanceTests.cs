namespace Wcs.Simulator.Tests;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.IndustrialIntelligence.Governance;

public sealed class IndustrialIntelligenceGovernanceTests
{
    [Fact]
    public void Production_IsAlwaysFailClosed()
    {
        var options = ValidOptions();
        options.AllowedEnvironments = ["Production", "IndustrialIntelligence"];

        var decision = IndustrialIntelligenceEnvironmentGuard.Evaluate("Production", options);

        Assert.False(decision.Allowed);
        Assert.Equal(AutomationLevel.L0, decision.EffectiveMaximumAutomationLevel);
    }

    [Fact]
    public void DisabledConfiguration_IsRejected()
    {
        var options = ValidOptions();
        options.Enabled = false;

        Assert.False(IndustrialIntelligenceEnvironmentGuard.Evaluate("IndustrialIntelligence", options).Allowed);
    }

    [Fact]
    public void UnapprovedEnvironment_IsRejected()
    {
        var options = ValidOptions();

        Assert.False(IndustrialIntelligenceEnvironmentGuard.Evaluate("Staging", options).Allowed);
    }

    [Fact]
    public void P0_RejectsAutomationAboveL1()
    {
        var options = ValidOptions();
        options.MaximumAutomationLevel = AutomationLevel.L2;

        var decision = IndustrialIntelligenceEnvironmentGuard.Evaluate("IndustrialIntelligence", options);

        Assert.False(decision.Allowed);
        Assert.Contains("L0/L1", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovedReadOnlyEnvironment_AllowsL1()
    {
        var decision = IndustrialIntelligenceEnvironmentGuard.Evaluate(
            "IndustrialIntelligence",
            ValidOptions());

        Assert.True(decision.Allowed);
        Assert.Equal(AutomationLevel.L1, decision.EffectiveMaximumAutomationLevel);
        Assert.Equal(IndustrialIntelligenceMode.ReadOnly, decision.EffectiveMode);
    }

    [Fact]
    public void InvalidBounds_AreFailClosed()
    {
        var options = ValidOptions();
        options.MaximumPendingProposals = 0;

        var decision = IndustrialIntelligenceEnvironmentGuard.Evaluate("IndustrialIntelligence", options);

        Assert.False(decision.Allowed);
        Assert.Contains("MaximumPendingProposals", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Sha256_IsDeterministic()
    {
        var first = Hashing.Sha256("canonical-value");
        var second = Hashing.Sha256("canonical-value");

        Assert.Equal(first, second);
        Assert.True(Hashing.IsSha256(first));
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void VersionedHashReference_RejectsInvalidHash()
    {
        Assert.Throws<ArgumentException>(() => VersionedHashReference.Create("v1", "bad-hash"));
    }

    [Fact]
    public void EvidenceReference_RequiresValidSha256()
    {
        Assert.Throws<ArgumentException>(() => EvidenceReference.Create(
            "e1", "contract", "model", "m1", "v1", "not-a-sha",
            DateTimeOffset.UtcNow, "tester", "corr-1"));
    }

    [Fact]
    public void ActorReason_RequiresActorAndReason()
    {
        Assert.Throws<ArgumentException>(() => ActorReason.Create("", "reason"));
        Assert.Throws<ArgumentException>(() => ActorReason.Create("actor", ""));
        var value = ActorReason.Create(" actor ", " reason ");
        Assert.Equal("actor", value.Actor);
        Assert.Equal("reason", value.Reason);
    }

    [Fact]
    public void BoundedQuery_RejectsUnboundedRequests()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedQuery.Create(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoundedQuery.Create(0, BoundedQuery.MaximumLimit + 1));
        Assert.Equal(100, BoundedQuery.Create(0, 100).Limit);
    }

    [Fact]
    public void AuditJournal_IsAppendOnlyAndRejectsDuplicateId()
    {
        var journal = new InMemoryIndustrialIntelligenceAuditJournal();
        var record = new IndustrialIntelligenceAuditRecord(
            "audit-1", "Register", "Model", "m1", "operator", "approved test",
            DateTimeOffset.UtcNow, "corr-1", Hashing.Sha256("payload"));

        journal.Append(record);

        Assert.Single(journal.Snapshot());
        Assert.Throws<InvalidOperationException>(() => journal.Append(record));
        Assert.Equal(record, journal.Snapshot()[0]);
    }

    [Fact]
    public void StatusApi_IsReadOnlyAndFailClosedOutsideApprovedEnvironment()
    {
        var controller = CreateController("IndustrialIntelligence", enabled: true, level: "L1");
        var result = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var response = Assert.IsType<IndustrialIntelligenceStatusResponse>(result.Value);

        Assert.True(response.ReadOnly);
        Assert.False(response.ControlWriteAllowed);
        Assert.False(response.ProductionAllowed);
        Assert.Equal("IDI-P0", response.Stage);

        var denied = CreateController("Production", enabled: true, level: "L1");
        Assert.IsType<NotFoundResult>(denied.GetStatus());
    }

    [Fact]
    public void Controller_ExposesOnlyTwoHttpGetActions()
    {
        var actionMethods = typeof(IndustrialIntelligenceController)
            .GetMethods()
            .Where(method => method.DeclaringType == typeof(IndustrialIntelligenceController))
            .Where(method => method.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any())
            .ToArray();

        Assert.Equal(2, actionMethods.Length);
        Assert.All(actionMethods, method =>
        {
            var attributes = method.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().ToArray();
            Assert.Single(attributes);
            Assert.Equal(["GET"], attributes[0].HttpMethods);
        });
    }

    private static IndustrialIntelligenceOptions ValidOptions() => new()
    {
        Enabled = true,
        Mode = IndustrialIntelligenceMode.ReadOnly,
        AllowedEnvironments = ["IndustrialIntelligence", "IndustrialIntelligenceLoadTest"],
        MaximumAutomationLevel = AutomationLevel.L1
    };

    private static IndustrialIntelligenceController CreateController(
        string environment,
        bool enabled,
        string level)
    {
        var values = new Dictionary<string, string?>
        {
            ["IndustrialIntelligence:Enabled"] = enabled.ToString(),
            ["IndustrialIntelligence:Mode"] = "ReadOnly",
            ["IndustrialIntelligence:AllowedEnvironments:0"] = "IndustrialIntelligence",
            ["IndustrialIntelligence:AllowedEnvironments:1"] = "IndustrialIntelligenceLoadTest",
            ["IndustrialIntelligence:MaximumAutomationLevel"] = level,
            ["IndustrialIntelligence:MaximumPendingProposals"] = "10000",
            ["IndustrialIntelligence:ProposalRetentionDays"] = "180",
            ["IndustrialIntelligence:EvidenceRetentionDays"] = "365",
            ["IndustrialIntelligence:DefaultInferenceTimeoutMs"] = "200",
            ["IndustrialIntelligence:MaximumModelPackageBytes"] = "268435456",
            ["IndustrialIntelligence:MaximumLoadedModels"] = "8",
            ["IndustrialIntelligence:MaximumConcurrentInference"] = "4",
            ["IndustrialIntelligence:FeatureSnapshotRetentionDays"] = "90",
            ["IndustrialIntelligence:MaximumDatasetRows"] = "5000000"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new IndustrialIntelligenceController(
            new TestHostEnvironment { EnvironmentName = environment },
            configuration);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Wcs.Host.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
