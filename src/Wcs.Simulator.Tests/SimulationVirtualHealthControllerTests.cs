namespace Wcs.Simulator.Tests;

using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Wcs.Host.Controllers;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualHealth;

public sealed class SimulationVirtualHealthControllerTests
{
    [Fact]
    public void Status_InProduction_Returns404()
    {
        var controller = new SimulationVirtualHealthController(
            new TestHostEnvironment { EnvironmentName = "Production" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.GetStatus(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActiveRun_CanInspectHealthFeatureForecastOutcomeAndAudit()
    {
        var configuration = BuildConfiguration();
        var environment = new TestHostEnvironment { EnvironmentName = "Simulation" };
        var governance = new SimulationGovernanceController(environment, configuration);
        var scenarios = new SimulationScenarioController(environment, configuration);
        var health = new SimulationVirtualHealthController(environment, configuration);
        var scenarioId = $"host-s6-{Guid.NewGuid():N}";
        var content = BuildScenarioJson(scenarioId);

        Assert.IsType<OkObjectResult>(governance.ValidateAndRegister(
            BuildGovernanceRequest(scenarioId, content)));
        var create = Assert.IsType<OkObjectResult>(scenarios.CreateRun(new CreateSimulationRunRequest
        {
            ScenarioId = scenarioId,
            Version = "1.0.0",
            ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            StartPaused = true
        }));
        var run = Assert.IsType<SimulationRunSnapshot>(create.Value);

        for (var index = 0; index < 4; index++)
            Assert.IsType<OkObjectResult>(await scenarios.Step(run.RunId, CancellationToken.None));

        var statusResult = Assert.IsType<OkObjectResult>(health.GetStatus(run.RunId));
        var status = Assert.IsType<VirtualHealthStatus>(statusResult.Value);
        Assert.Equal(1, status.AssetCount);
        Assert.Equal(49, status.SampleCount);
        Assert.Equal(1, status.ForecastCount);
        Assert.Equal(1, status.OutcomeCount);

        var assetsResult = Assert.IsType<OkObjectResult>(health.ListAssets(run.RunId));
        var assets = Assert.IsAssignableFrom<IReadOnlyList<VirtualHealthAssetSnapshot>>(assetsResult.Value);
        var asset = Assert.Single(assets);
        Assert.Equal("RGV-HOST", asset.AssetId);

        var samplesResult = Assert.IsType<OkObjectResult>(health.ListSamples(run.RunId, "RGV-HOST"));
        var samples = Assert.IsAssignableFrom<IReadOnlyList<VirtualHealthSampleSnapshot>>(samplesResult.Value);
        Assert.Equal(49, samples.Count);

        var featureResult = Assert.IsType<OkObjectResult>(health.GetFeature(run.RunId, "RGV-HOST"));
        var feature = Assert.IsType<VirtualHealthFeatureSnapshot>(featureResult.Value);
        Assert.True(feature.Valid, feature.Reason);
        Assert.Equal(14, feature.FeatureNames.Count);

        var forecastsResult = Assert.IsType<OkObjectResult>(health.ListForecasts(run.RunId, "RGV-HOST"));
        var forecasts = Assert.IsAssignableFrom<IReadOnlyList<VirtualHealthForecastOracleSnapshot>>(forecastsResult.Value);
        Assert.Single(forecasts);

        var outcomesResult = Assert.IsType<OkObjectResult>(health.ListOutcomes(run.RunId, "RGV-HOST"));
        var outcomes = Assert.IsAssignableFrom<IReadOnlyList<VirtualHealthOutcomeSnapshot>>(outcomesResult.Value);
        Assert.Single(outcomes);

        var auditResult = Assert.IsType<OkObjectResult>(health.ListAudit(run.RunId, 20));
        var audit = Assert.IsAssignableFrom<IReadOnlyList<VirtualHealthAuditRecord>>(auditResult.Value);
        Assert.True(audit.Count >= 4);
    }

    [Fact]
    public void UnknownRun_Returns404()
    {
        var controller = new SimulationVirtualHealthController(
            new TestHostEnvironment { EnvironmentName = "Simulation" },
            BuildConfiguration());

        Assert.IsType<NotFoundResult>(controller.ListAssets(Guid.NewGuid()));
    }

    private static ValidateSimulationScenarioRequest BuildGovernanceRequest(string scenarioId, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var createdAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        return new ValidateSimulationScenarioRequest
        {
            Manifest = new SimulationScenarioManifest
            {
                SchemaVersion = 1,
                ScenarioId = scenarioId,
                Version = "1.0.0",
                Seed = 20260801,
                ScenarioFile = $"{scenarioId}.json",
                ContentSha256 = SimulationScenarioValidator.ComputeSha256(bytes),
                CreatedAtUtc = createdAt,
                Source = "host-s6-controller-test",
                ApprovedBy = "ci",
                ApprovedAtUtc = createdAt.AddMinutes(1)
            },
            ContentBase64 = Convert.ToBase64String(bytes)
        };
    }

    private static string BuildScenarioJson(string scenarioId) => $$"""
    {
      "SchemaVersion": 1,
      "ScenarioId": "{{scenarioId}}",
      "Version": "1.0.0",
      "Seed": 20260801,
      "StartTimeUtc": "2026-08-01T00:00:00+00:00",
      "DurationMilliseconds": 176400000,
      "StopOnAssertionFailure": true,
      "Actions": [
        { "Id":"define", "AtMilliseconds":0, "Order":0, "Kind":"health.asset.define", "Target":"RGV-HOST", "Payload":{"InitialHealthScore":100,"InitialFusionRiskScore":0.05,"IndependentSourceCount":1} },
        { "Id":"degrade", "AtMilliseconds":172800000, "Order":0, "Kind":"health.profile.linear", "Target":"RGV-HOST", "Payload":{"TargetHealthScore":55,"TargetFusionRiskScore":0.75,"SampleIntervalMilliseconds":3600000,"Reason":"host-degradation"} },
        { "Id":"forecast", "AtMilliseconds":172800000, "Order":1, "Kind":"health.forecast.oracle", "Target":"RGV-HOST", "Payload":{"FailureProbability24Hours":0.1,"FailureProbability72Hours":0.2,"FailureProbability168Hours":0.4,"RulLowerHours":80,"RulMedianHours":140,"RulUpperHours":220,"Phase":"degradation"} },
        { "Id":"outcome", "AtMilliseconds":172800000, "Order":2, "Kind":"health.outcome.record", "Target":"RGV-HOST", "Payload":{"Kind":"CensoredNoFailure","Note":"host-window"} },
        { "Id":"future", "AtMilliseconds":176400000, "Order":0, "Kind":"event.emit", "Target":"keep-active", "Payload":{} }
      ],
      "Assertions": []
    }
    """;

    private static IConfiguration BuildConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["Simulator:Enabled"] = "true",
            ["SimulationGovernance:Enabled"] = "true",
            ["SimulationGovernance:ScenarioDirectory"] = "data/simulation-scenarios",
            ["SimulationGovernance:MaximumScenarioBytes"] = "1048576",
            ["SimulationGovernance:MaximumRegisteredScenarioVersions"] = "10000",
            ["SimulationGovernance:MaximumEvidenceRecords"] = "10000",
            ["SimulationGovernance:MaximumEvidenceValueCharacters"] = "4096",
            ["SimulationScenarioEngine:MaximumTimelineItems"] = "100000",
            ["SimulationScenarioEngine:MaximumStateEntries"] = "10000",
            ["SimulationScenarioEngine:MaximumStateValueCharacters"] = "4096",
            ["SimulationScenarioEngine:MaximumCheckpointBytes"] = "16777216",
            ["SimulationScenarioEngine:MaximumSpeedFactor"] = "1000",
            ["SimulationRunRegistry:MaximumRuns"] = "1000",
            ["SimulationVirtualHealth:MaximumAssets"] = "256",
            ["SimulationVirtualHealth:MaximumSamplesPerAsset"] = "2048",
            ["SimulationVirtualHealth:MaximumForecastsPerAsset"] = "512",
            ["SimulationVirtualHealth:MaximumOutcomesPerAsset"] = "128",
            ["SimulationVirtualHealth:MaximumGeneratedSamplesPerAction"] = "1024",
            ["SimulationVirtualHealth:MaximumAuditRecords"] = "10000",
            ["SimulationVirtualHealth:ForecastMinimumHistoryPoints"] = "48",
            ["SimulationVirtualHealth:ForecastMinimumHistorySpanHours"] = "24",
            ["SimulationVirtualHealth:ForecastMaximumHistoryPoints"] = "2000",
            ["SimulationVirtualHealth:TrendWindowSize"] = "12",
            ["SimulationVirtualHealth:TrendChangeThreshold"] = "2",
            ["SimulationVirtualHealth:HealthyMinimumScore"] = "85",
            ["SimulationVirtualHealth:AttentionMinimumScore"] = "70",
            ["SimulationVirtualHealth:DegradedMinimumScore"] = "40",
            ["SimulationVirtualHealth:MaximumRulHours"] = "17520"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Simulation";
        public string ApplicationName { get; set; } = "Wcs.Simulator.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
