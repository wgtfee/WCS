namespace Wcs.Simulator.Tests;

using System.Text;
using Wcs.Simulator.Governance;

public sealed class SimulationGovernanceTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ApprovedAtUtc = new(2026, 7, 29, 0, 5, 0, TimeSpan.Zero);

    [Fact]
    public void DeterministicRandom_WithSameSeed_ProducesSameSequence()
    {
        var first = new DeterministicSimulationRandom(20260729);
        var second = new DeterministicSimulationRandom(20260729);

        var firstValues = Enumerable.Range(0, 100).Select(_ => first.NextUInt64()).ToArray();
        var secondValues = Enumerable.Range(0, 100).Select(_ => second.NextUInt64()).ToArray();

        Assert.Equal(firstValues, secondValues);
    }

    [Fact]
    public void DeterministicRandom_WithDifferentSeed_ProducesDifferentSequence()
    {
        var first = new DeterministicSimulationRandom(20260729);
        var second = new DeterministicSimulationRandom(20260730);

        Assert.NotEqual(first.NextUInt64(), second.NextUInt64());
    }

    [Fact]
    public void BoundaryGuard_AlwaysDeniesProduction()
    {
        var options = EnabledOptions();

        var decision = SimulationBoundaryGuard.Evaluate("Production", options, simulatorEnabled: true);

        Assert.False(decision.Allowed);
        Assert.Equal("production-denied", decision.Code);
    }

    [Fact]
    public void BoundaryGuard_DeniesProductionBeforeEvaluatingInvalidAllowList()
    {
        var options = EnabledOptions();
        options.AllowedEnvironments = ["Simulation", "Production"];

        var decision = SimulationBoundaryGuard.Evaluate("Production", options, simulatorEnabled: true);

        Assert.False(decision.Allowed);
        Assert.Equal("production-denied", decision.Code);
    }

    [Theory]
    [InlineData(false, true, "governance-disabled")]
    [InlineData(true, false, "simulator-disabled")]
    public void BoundaryGuard_RequiresBothSwitches(bool governanceEnabled, bool simulatorEnabled, string expectedCode)
    {
        var options = EnabledOptions();
        options.Enabled = governanceEnabled;

        var decision = SimulationBoundaryGuard.Evaluate("Simulation", options, simulatorEnabled);

        Assert.False(decision.Allowed);
        Assert.Equal(expectedCode, decision.Code);
    }

    [Fact]
    public void BoundaryGuard_AllowsOnlyApprovedEnvironment()
    {
        var options = EnabledOptions();

        Assert.True(SimulationBoundaryGuard.Evaluate("Simulation", options, true).Allowed);
        Assert.False(SimulationBoundaryGuard.Evaluate("Development", options, true).Allowed);
    }

    [Fact]
    public void Options_RejectProductionAllowList()
    {
        var options = EnabledOptions();
        options.AllowedEnvironments = ["Simulation", "Production"];

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("Production", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RejectUnboundedRegistryCapacity()
    {
        var options = EnabledOptions();
        options.MaximumRegisteredScenarioVersions = 100_001;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("MaximumRegisteredScenarioVersions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidPackage_RegistersIdempotentlyAndProducesStableHashes()
    {
        var options = EnabledOptions();
        var registry = new SimulationScenarioRegistry();
        var package = BuildPackage("{\"actions\":[]}");

        var first = registry.Register(package, options, CreatedAtUtc.AddHours(1));
        var second = registry.Register(package, options, CreatedAtUtc.AddHours(2));

        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
        Assert.Single(registry.List());
    }

    [Fact]
    public void Registry_RejectsSameVersionWithDifferentContent()
    {
        var options = EnabledOptions();
        var registry = new SimulationScenarioRegistry();
        registry.Register(BuildPackage("{\"actions\":[]}"), options);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(BuildPackage("{\"actions\":[{\"type\":\"fault\"}]}"), options));

        Assert.Contains("immutable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_RejectsNewVersionAfterCapacityIsReached()
    {
        var options = EnabledOptions();
        options.MaximumRegisteredScenarioVersions = 1;
        var registry = new SimulationScenarioRegistry();
        registry.Register(BuildPackage("{\"actions\":[]}", "capacity-a"), options);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(BuildPackage("{\"actions\":[]}", "capacity-b"), options));

        Assert.Contains("MaximumRegisteredScenarioVersions", exception.Message, StringComparison.Ordinal);
        Assert.Single(registry.List());
    }

    [Fact]
    public void Validator_RejectsPathTraversal()
    {
        var options = EnabledOptions();
        var package = BuildPackage("{\"actions\":[]}");
        package.Manifest.ScenarioFile = "../outside.json";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SimulationScenarioValidator.Validate(package, options));

        Assert.Contains("traversal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsContentHashMismatch()
    {
        var options = EnabledOptions();
        var package = BuildPackage("{\"actions\":[]}");
        package.Manifest.ContentSha256 = new string('0', 64);

        Assert.Throws<InvalidOperationException>(() =>
            SimulationScenarioValidator.Validate(package, options));
    }

    [Fact]
    public void Validator_RejectsOversizedApprovalMetadata()
    {
        var options = EnabledOptions();
        var package = BuildPackage("{\"actions\":[]}");
        package.Manifest.ApprovedBy = new string('a', 257);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SimulationScenarioValidator.Validate(package, options));

        Assert.Contains("ApprovedBy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_WithSameInputs_ProducesStableHash()
    {
        var options = EnabledOptions();
        var scenario = new SimulationScenarioRegistry().Register(BuildPackage("{\"actions\":[]}"), options, CreatedAtUtc);
        var records = new[]
        {
            new SimulationEvidenceRecord(1, "governance", "validated", "true", CreatedAtUtc.AddMinutes(1)),
            new SimulationEvidenceRecord(2, "control", "plcWrites", "0", CreatedAtUtc.AddMinutes(2))
        };

        var first = SimulationEvidenceEnvelope.Create(
            scenario,
            CreatedAtUtc,
            CreatedAtUtc.AddMinutes(3),
            records,
            options);
        var second = SimulationEvidenceEnvelope.Create(
            scenario,
            CreatedAtUtc,
            CreatedAtUtc.AddMinutes(3),
            records.Reverse(),
            options);

        Assert.Equal(first.EvidenceHash, second.EvidenceHash);
        Assert.Equal([1L, 2L], first.Records.Select(record => record.Sequence).ToArray());
    }

    [Fact]
    public void Evidence_RejectsDuplicateSequence()
    {
        var options = EnabledOptions();
        var scenario = new SimulationScenarioRegistry().Register(BuildPackage("{\"actions\":[]}"), options);
        var records = new[]
        {
            new SimulationEvidenceRecord(1, "governance", "a", "1", CreatedAtUtc),
            new SimulationEvidenceRecord(1, "governance", "b", "2", CreatedAtUtc)
        };

        Assert.Throws<InvalidOperationException>(() =>
            SimulationEvidenceEnvelope.Create(
                scenario,
                CreatedAtUtc,
                CreatedAtUtc.AddMinutes(1),
                records,
                options));
    }

    [Fact]
    public void Evidence_RejectsOversizedValue()
    {
        var options = EnabledOptions();
        options.MaximumEvidenceValueCharacters = 4;
        var scenario = new SimulationScenarioRegistry().Register(BuildPackage("{\"actions\":[]}"), options);
        var records = new[]
        {
            new SimulationEvidenceRecord(1, "governance", "value", "12345", CreatedAtUtc)
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SimulationEvidenceEnvelope.Create(
                scenario,
                CreatedAtUtc,
                CreatedAtUtc.AddMinutes(1),
                records,
                options));

        Assert.Contains("MaximumEvidenceValueCharacters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_RejectsOccurrenceOutsideInterval()
    {
        var options = EnabledOptions();
        var scenario = new SimulationScenarioRegistry().Register(BuildPackage("{\"actions\":[]}"), options);
        var records = new[]
        {
            new SimulationEvidenceRecord(1, "governance", "time", "invalid", CreatedAtUtc.AddMinutes(2))
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SimulationEvidenceEnvelope.Create(
                scenario,
                CreatedAtUtc,
                CreatedAtUtc.AddMinutes(1),
                records,
                options));

        Assert.Contains("inside the evidence interval", exception.Message, StringComparison.Ordinal);
    }

    private static SimulationGovernanceOptions EnabledOptions() => new()
    {
        Enabled = true,
        MaximumScenarioBytes = 1024 * 1024,
        MaximumRegisteredScenarioVersions = 100,
        MaximumEvidenceRecords = 100,
        MaximumEvidenceValueCharacters = 4096,
        AllowedEnvironments = ["Simulation", "SimulationLoadTest"]
    };

    private static SimulationScenarioPackage BuildPackage(
        string content,
        string scenarioId = "governance-smoke",
        string version = "1.0.0")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new SimulationScenarioPackage(
            new SimulationScenarioManifest
            {
                SchemaVersion = 1,
                ScenarioId = scenarioId,
                Version = version,
                Seed = 20260729,
                ScenarioFile = $"{scenarioId}.json",
                ContentSha256 = SimulationScenarioValidator.ComputeSha256(bytes),
                CreatedAtUtc = CreatedAtUtc,
                Source = "repository-test",
                ApprovedBy = "ci",
                ApprovedAtUtc = ApprovedAtUtc
            },
            bytes);
    }
}
