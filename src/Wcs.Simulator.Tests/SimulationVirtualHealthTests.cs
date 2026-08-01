namespace Wcs.Simulator.Tests;

using System.Text.Json;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualHealth;

public sealed class SimulationVirtualHealthTests
{
    private static readonly DateTimeOffset StartTimeUtc = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private const long Hour = 3_600_000;

    [Fact]
    public void LinearDegradation_BuildsGovernedForecastFeatureVector()
    {
        var (runtime, _) = Runtime();
        runtime.DefineAsset(new VirtualHealthAssetDefinition("RGV-01", 100, 0.05, 1), 0, StartTimeUtc);

        var generated = runtime.GenerateLinearProfile(
            "RGV-01", 55, 0.80, Hour, 48 * Hour, StartTimeUtc.AddHours(48), "bearing-degradation");

        Assert.Equal(48, generated.Count);
        var asset = runtime.GetAsset("RGV-01");
        Assert.Equal(49, asset.SampleCount);
        Assert.Equal(55, asset.HealthScore, 6);
        Assert.Equal(AssetHealthGrade.Degraded, asset.Grade);

        var feature = runtime.GetFeatureSnapshot("RGV-01");
        Assert.True(feature.Valid, feature.Reason);
        Assert.Equal(14, feature.FeatureNames.Count);
        Assert.Equal(14, feature.Values.Count);
        Assert.Equal(49, feature.SampleCount);
        Assert.Equal(48, feature.HistorySpanHours, 6);
        Assert.Equal(AssetHealthTrendDirection.Deteriorating, runtime.GetTrend("RGV-01").Direction);
    }

    [Fact]
    public void ForecastOracle_RejectsInvalidV39OutputContract()
    {
        var (runtime, _) = Runtime();
        runtime.DefineAsset(new VirtualHealthAssetDefinition("RGV-02", 80, 0.3), 0, StartTimeUtc);

        Assert.Throws<InvalidOperationException>(() => runtime.AddForecastOracle(
            "RGV-02",
            new VirtualHealthForecastOracleDefinition(0.5, 0.4, 0.8, 10, 20, 30),
            Hour,
            StartTimeUtc.AddHours(1)));

        Assert.Throws<InvalidOperationException>(() => runtime.AddForecastOracle(
            "RGV-02",
            new VirtualHealthForecastOracleDefinition(0.1, 0.2, 0.3, 30, 20, 40),
            Hour,
            StartTimeUtc.AddHours(1)));
    }

    [Fact]
    public void DeteriorationForecasts_IncreaseProbabilityAndDecreaseRul()
    {
        var (runtime, _) = Runtime();
        runtime.DefineAsset(new VirtualHealthAssetDefinition("RGV-03", 90, 0.2), 0, StartTimeUtc);
        runtime.AddForecastOracle("RGV-03",
            new VirtualHealthForecastOracleDefinition(0.05, 0.10, 0.20, 200, 300, 400),
            24 * Hour, StartTimeUtc.AddHours(24));
        runtime.AddForecastOracle("RGV-03",
            new VirtualHealthForecastOracleDefinition(0.10, 0.25, 0.45, 120, 220, 320),
            48 * Hour, StartTimeUtc.AddHours(48));
        runtime.AddForecastOracle("RGV-03",
            new VirtualHealthForecastOracleDefinition(0.25, 0.50, 0.80, 40, 100, 180),
            72 * Hour, StartTimeUtc.AddHours(72));

        Assert.True(runtime.ForecastContractsValid("RGV-03"));
        Assert.True(runtime.FailureProbabilitiesAreNonDecreasing("RGV-03"));
        Assert.True(runtime.RulMedianIsNonIncreasing("RGV-03"));
    }

    [Fact]
    public void MaintenanceRestore_ImprovesHealthAndRecordsPreventiveOutcome()
    {
        var (runtime, _) = Runtime();
        runtime.DefineAsset(new VirtualHealthAssetDefinition("RGV-04", 95, 0.1), 0, StartTimeUtc);
        runtime.GenerateLinearProfile("RGV-04", 35, 0.90, Hour, 24 * Hour, StartTimeUtc.AddHours(24));
        Assert.Equal(AssetHealthGrade.Critical, runtime.GetAsset("RGV-04").Grade);

        runtime.RestoreAfterMaintenance("RGV-04", 92, 0.12, 1, 25 * Hour, StartTimeUtc.AddHours(25), "bearing-replaced");
        var outcome = runtime.RecordOutcome("RGV-04", VirtualHealthOutcomeKind.PreventiveMaintenance,
            25 * Hour, StartTimeUtc.AddHours(25), "bearing-replaced");

        Assert.Equal(AssetHealthGrade.Healthy, runtime.GetAsset("RGV-04").Grade);
        Assert.Equal(AssetHealthTrendDirection.Improving, runtime.GetTrend("RGV-04").Direction);
        Assert.Equal(VirtualHealthOutcomeKind.PreventiveMaintenance, outcome.Kind);
    }

    [Fact]
    public void CensoredOutcome_IsRetainedAsSimulationEvidence()
    {
        var (runtime, _) = Runtime();
        runtime.DefineAsset(new VirtualHealthAssetDefinition("RGV-05", 88, 0.2), 0, StartTimeUtc);
        var outcome = runtime.RecordOutcome("RGV-05", VirtualHealthOutcomeKind.CensoredNoFailure,
            168 * Hour, StartTimeUtc.AddHours(168), "observation-window-ended");

        Assert.Equal(VirtualHealthOutcomeKind.CensoredNoFailure, outcome.Kind);
        Assert.Single(runtime.ListOutcomes("RGV-05"));
        Assert.Contains(runtime.ListAudit(), item => item.Operation == "outcome.record");
    }

    [Fact]
    public void Runtime_EnforcesAssetSampleForecastAndOutcomeCapacity()
    {
        var options = Options();
        options.MaximumAssets = 1;
        options.MaximumSamplesPerAsset = 2;
        options.MaximumForecastsPerAsset = 1;
        options.MaximumOutcomesPerAsset = 1;
        options.ForecastMinimumHistoryPoints = 2;
        options.ForecastMaximumHistoryPoints = 2;
        options.TrendWindowSize = 2;
        var runtime = new VirtualHealthRuntime(new SimulationStateStore(EngineOptions()), options);
        runtime.DefineAsset(new VirtualHealthAssetDefinition("A1", 90, 0.1), 0, StartTimeUtc);

        Assert.Throws<InvalidOperationException>(() => runtime.DefineAsset(
            new VirtualHealthAssetDefinition("A2", 90, 0.1), 0, StartTimeUtc));
        runtime.RecordSample("A1", 80, 0.2, 1, Hour, StartTimeUtc.AddHours(1));
        Assert.Throws<InvalidOperationException>(() => runtime.RecordSample(
            "A1", 70, 0.3, 1, 2 * Hour, StartTimeUtc.AddHours(2)));

        runtime.AddForecastOracle("A1", new VirtualHealthForecastOracleDefinition(0.1, 0.2, 0.3, 10, 20, 30),
            Hour, StartTimeUtc.AddHours(1));
        Assert.Throws<InvalidOperationException>(() => runtime.AddForecastOracle(
            "A1", new VirtualHealthForecastOracleDefinition(0.2, 0.3, 0.4, 8, 18, 28),
            2 * Hour, StartTimeUtc.AddHours(2)));

        runtime.RecordOutcome("A1", VirtualHealthOutcomeKind.CensoredNoFailure, Hour, StartTimeUtc.AddHours(1), "done");
        Assert.Throws<InvalidOperationException>(() => runtime.RecordOutcome(
            "A1", VirtualHealthOutcomeKind.ObservedFailure, 2 * Hour, StartTimeUtc.AddHours(2), "failure"));
    }

    [Fact]
    public void StateRestore_PreservesHealthForecastOutcomeAuditAndHash()
    {
        var options = Options();
        var state = new SimulationStateStore(EngineOptions());
        var runtime = new VirtualHealthRuntime(state, options);
        runtime.DefineAsset(new VirtualHealthAssetDefinition("A1", 100, 0.05), 0, StartTimeUtc);
        runtime.GenerateLinearProfile("A1", 60, 0.7, Hour, 48 * Hour, StartTimeUtc.AddHours(48));
        runtime.AddForecastOracle("A1", new VirtualHealthForecastOracleDefinition(0.1, 0.2, 0.4, 50, 100, 160),
            48 * Hour, StartTimeUtc.AddHours(48));
        runtime.RecordOutcome("A1", VirtualHealthOutcomeKind.CensoredNoFailure,
            48 * Hour, StartTimeUtc.AddHours(48), "window-end");

        var canonical = state.ToCanonicalJson();
        var restoredState = SimulationStateStore.FromCanonicalJson(canonical, EngineOptions());
        var restored = new VirtualHealthRuntime(restoredState, options);

        Assert.Equal(canonical, restoredState.ToCanonicalJson());
        Assert.Equal(JsonSerializer.Serialize(runtime.ListAssets()), JsonSerializer.Serialize(restored.ListAssets()));
        Assert.Equal(JsonSerializer.Serialize(runtime.ListSamples("A1")), JsonSerializer.Serialize(restored.ListSamples("A1")));
        Assert.Equal(JsonSerializer.Serialize(runtime.ListForecasts("A1")), JsonSerializer.Serialize(restored.ListForecasts("A1")));
        Assert.Equal(JsonSerializer.Serialize(runtime.ListOutcomes("A1")), JsonSerializer.Serialize(restored.ListOutcomes("A1")));
        Assert.Equal(state.ComputeHash(), restoredState.ComputeHash());
    }

    [Fact]
    public async Task ScenarioReplay_HealthDegradationAndRul_IsEquivalent()
    {
        var definition = Scenario();
        var registered = Register(definition);
        var engine = new SimulationScenarioEngine(
            VirtualHealthScenarioHandlers.CreateActions(Options()),
            VirtualHealthScenarioHandlers.CreateAssertions(Options()),
            EngineOptions());

        var replay = await engine.ReplayTwiceAsync(registered, definition);

        Assert.True(replay.Equivalent);
        Assert.True(replay.First.Success);
        Assert.Equal(replay.FirstStateHash, replay.SecondStateHash);
        Assert.Equal(replay.FirstEvidenceHash, replay.SecondEvidenceHash);
        Assert.All(replay.First.Assertions, static assertion => Assert.True(assertion.Passed, assertion.Message));
    }

    private static (VirtualHealthRuntime Runtime, SimulationStateStore State) Runtime()
    {
        var state = new SimulationStateStore(EngineOptions());
        return (new VirtualHealthRuntime(state, Options()), state);
    }

    private static SimulationScenarioDefinition Scenario() => new()
    {
        ScenarioId = "synthetic-health-rul",
        Version = "1.0.0",
        Seed = 20260801,
        StartTimeUtc = StartTimeUtc,
        DurationMilliseconds = 72 * Hour,
        Actions =
        [
            Action("define", 0, 0, "health.asset.define", "RGV-S6", "{\"InitialHealthScore\":100,\"InitialFusionRiskScore\":0.05,\"IndependentSourceCount\":1}"),
            Action("degrade-48", 48 * Hour, 0, "health.profile.linear", "RGV-S6", "{\"TargetHealthScore\":55,\"TargetFusionRiskScore\":0.75,\"SampleIntervalMilliseconds\":3600000,\"Reason\":\"bearing-wear\"}"),
            Action("forecast-48", 48 * Hour, 1, "health.forecast.oracle", "RGV-S6", "{\"FailureProbability24Hours\":0.10,\"FailureProbability72Hours\":0.25,\"FailureProbability168Hours\":0.45,\"RulLowerHours\":120,\"RulMedianHours\":180,\"RulUpperHours\":260,\"Phase\":\"degradation\"}"),
            Action("degrade-72", 72 * Hour, 0, "health.profile.linear", "RGV-S6", "{\"TargetHealthScore\":30,\"TargetFusionRiskScore\":0.95,\"SampleIntervalMilliseconds\":3600000,\"Reason\":\"bearing-wear\"}"),
            Action("forecast-72", 72 * Hour, 1, "health.forecast.oracle", "RGV-S6", "{\"FailureProbability24Hours\":0.25,\"FailureProbability72Hours\":0.50,\"FailureProbability168Hours\":0.80,\"RulLowerHours\":40,\"RulMedianHours\":100,\"RulUpperHours\":160,\"Phase\":\"degradation\"}"),
            Action("outcome", 72 * Hour, 2, "health.outcome.record", "RGV-S6", "{\"Kind\":\"ObservedFailure\",\"Note\":\"synthetic-bearing-failure\"}")
        ],
        Assertions =
        [
            Assertion("grade", 72 * Hour, 0, "health.asset.grade", "RGV-S6", "\"Critical\""),
            Assertion("score", 72 * Hour, 1, "health.asset.score.at-most", "RGV-S6", "30"),
            Assertion("samples", 72 * Hour, 2, "health.sample.count", "RGV-S6", "73"),
            Assertion("trend", 72 * Hour, 3, "health.trend.direction", "RGV-S6", "\"Deteriorating\""),
            Assertion("feature", 72 * Hour, 4, "health.feature.valid", "RGV-S6", "true"),
            Assertion("contract", 72 * Hour, 5, "health.forecast.contract.valid", "RGV-S6", "true"),
            Assertion("rul", 72 * Hour, 6, "health.rul.nonincreasing", "RGV-S6", "true"),
            Assertion("probability", 72 * Hour, 7, "health.probability.nondecreasing", "RGV-S6", "true"),
            Assertion("outcome-kind", 72 * Hour, 8, "health.outcome.kind", "RGV-S6", "\"ObservedFailure\"")
        ]
    };

    private static SimulationActionDefinition Action(
        string id, long at, int order, string kind, string target, string payload) => new()
    {
        Id = id,
        AtMilliseconds = at,
        Order = order,
        Kind = kind,
        Target = target,
        Payload = Json(payload)
    };

    private static SimulationAssertionDefinition Assertion(
        string id, long at, int order, string kind, string target, string expected) => new()
    {
        Id = id,
        AtMilliseconds = at,
        Order = order,
        Kind = kind,
        Target = target,
        Expected = Json(expected)
    };

    private static RegisteredSimulationScenario Register(SimulationScenarioDefinition definition)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(definition);
        var manifest = new SimulationScenarioManifest
        {
            SchemaVersion = 1,
            ScenarioId = definition.ScenarioId,
            Version = definition.Version,
            Seed = definition.Seed,
            ScenarioFile = "synthetic-health-rul.json",
            ContentSha256 = SimulationScenarioValidator.ComputeSha256(content),
            CreatedAtUtc = StartTimeUtc.AddHours(-1),
            Source = "s6-test",
            ApprovedBy = "ci",
            ApprovedAtUtc = StartTimeUtc.AddMinutes(-30)
        };
        return new SimulationScenarioRegistry().Register(
            new SimulationScenarioPackage(manifest, content),
            new SimulationGovernanceOptions
            {
                Enabled = true,
                MaximumScenarioBytes = 16 * 1024 * 1024,
                MaximumRegisteredScenarioVersions = 100,
                MaximumEvidenceRecords = 100_000,
                MaximumEvidenceValueCharacters = 16_384,
                AllowedEnvironments = ["Simulation"]
            },
            StartTimeUtc);
    }

    private static SimulationScenarioEngineOptions EngineOptions() => new()
    {
        MaximumTimelineItems = 10_000,
        MaximumStateEntries = 100_000,
        MaximumStateValueCharacters = 4_096,
        MaximumCheckpointBytes = 64 * 1024 * 1024,
        MaximumSpeedFactor = 10_000
    };

    private static VirtualHealthOptions Options() => new()
    {
        MaximumAssets = 32,
        MaximumSamplesPerAsset = 2_048,
        MaximumForecastsPerAsset = 128,
        MaximumOutcomesPerAsset = 32,
        MaximumGeneratedSamplesPerAction = 1_024,
        MaximumAuditRecords = 500,
        ForecastMinimumHistoryPoints = 48,
        ForecastMinimumHistorySpanHours = 24,
        ForecastMaximumHistoryPoints = 2_000,
        TrendWindowSize = 12,
        TrendChangeThreshold = 2,
        HealthyMinimumScore = 85,
        AttentionMinimumScore = 70,
        DegradedMinimumScore = 40,
        MaximumRulHours = 17_520
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
