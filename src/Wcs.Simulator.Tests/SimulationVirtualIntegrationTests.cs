namespace Wcs.Simulator.Tests;

using System.Text.Json;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;
using Wcs.Simulator.VirtualHealth;
using Wcs.Simulator.VirtualIntegration;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

public sealed class SimulationVirtualIntegrationTests
{
    private static readonly DateTimeOffset StartTimeUtc = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefineMission_ProvisionsAllS2ToS6VirtualResources()
    {
        var state = State();
        var runtime = Runtime(state);

        var mission = runtime.DefineMission(Definition("M1"), 0, StartTimeUtc);

        Assert.Equal(VirtualIntegrationMissionState.Defined, mission.State);
        Assert.Equal(8, new VirtualPlcRuntime(state, PlcOptions()).GetBlock("PLC1.DB100").Size);
        Assert.Equal(2, new VirtualRgvRuntime(state, RgvOptions()).ListSegments().Count);
        Assert.Single(new VirtualRgvRuntime(state, RgvOptions()).ListVehicles());
        Assert.Equal(2, new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions()).ListZones().Count);
        Assert.Single(new VirtualExternalRuntime(state, ExternalOptions()).ListEndpoints());
        Assert.Single(new VirtualHealthRuntime(state, HealthOptions()).ListAssets());
        Assert.Single(runtime.ListMissions());
    }

    [Fact]
    public void DispatchMission_WritesRequestLoadsVehicleAndReservesWholeRoute()
    {
        var state = State();
        var runtime = Runtime(state);
        runtime.DefineMission(Definition("M1"), 0, StartTimeUtc);

        var dispatched = runtime.DispatchMission("M1", 10, At(10));

        Assert.Equal(VirtualIntegrationMissionState.Dispatched, dispatched.State);
        var block = new VirtualPlcRuntime(state, PlcOptions()).GetBlock("PLC1.DB100");
        Assert.Equal(1, block.Data[0]);
        var vehicle = new VirtualRgvRuntime(state, RgvOptions()).GetVehicle("RGV1");
        Assert.Equal("LOAD1", vehicle.LoadId);
        Assert.Equal(2, vehicle.RouteSegmentIds.Count);
        var reservations = new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions())
            .ListReservations(true, 10);
        Assert.Equal(2, reservations.Count);
        Assert.All(reservations, item => Assert.Equal("RGV1", item.VehicleId));
    }

    [Fact]
    public void AdvanceMission_ReleasesPassedReservationsAndCompletesAtDestination()
    {
        var state = State();
        var runtime = Runtime(state);
        runtime.DefineMission(Definition("M1"), 0, StartTimeUtc);
        runtime.DispatchMission("M1", 10, At(10));

        var moving = runtime.AdvanceMission("M1", 1_010, At(1_010));
        Assert.Equal(VirtualIntegrationMissionState.Moving, moving.State);
        Assert.Single(new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions())
            .ListReservations(true, 1_010));

        var completed = runtime.AdvanceMission("M1", 2_010, At(2_010));

        Assert.Equal(VirtualIntegrationMissionState.Completed, completed.State);
        var vehicle = new VirtualRgvRuntime(state, RgvOptions()).GetVehicle("RGV1");
        Assert.True(vehicle.IsAtNode);
        Assert.Equal("N3", vehicle.CurrentNodeId);
        Assert.Null(vehicle.LoadId);
        Assert.Empty(new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions())
            .ListReservations(true, 2_010));
        Assert.Equal(1, new VirtualPlcRuntime(state, PlcOptions()).GetBlock("PLC1.DB100").Data[1]);
    }

    [Fact]
    public void RepeatedAcknowledgement_IsExactlyOnceAcrossExternalAndHealthOutcome()
    {
        var state = State();
        var runtime = CompletedMission(state);

        var first = runtime.AcknowledgeMission("M1", 2_100, At(2_100));
        var second = runtime.AcknowledgeMission("M1", 2_200, At(2_200));

        Assert.Equal(VirtualIntegrationMissionState.Acknowledged, first.State);
        Assert.Equal(VirtualIntegrationMissionState.Acknowledged, second.State);
        var requests = new VirtualExternalRuntime(state, ExternalOptions()).ListRequests();
        var request = Assert.Single(requests);
        Assert.Equal(VirtualExternalRequestState.Succeeded, request.State);
        Assert.Single(new VirtualHealthRuntime(state, HealthOptions()).ListOutcomes("ASSET1"));
        Assert.Equal(1, new VirtualPlcRuntime(state, PlcOptions()).GetBlock("PLC1.DB100").Data[2]);
        Assert.True(runtime.GetConsistency("M1", 2_200).IsConsistent);
    }

    [Fact]
    public void ExternalTransientTimeout_RetriesInsideAckAndStillRemainsExactlyOnce()
    {
        var state = State();
        var runtime = CompletedMission(state);
        var external = new VirtualExternalRuntime(state, ExternalOptions());
        external.ApplyFault(new VirtualExternalFaultDefinition(
            "ACK-TIMEOUT", "MES1", VirtualExternalFaultKind.Timeout, 2_100, 2_500),
            2_000, At(2_000));

        runtime.AcknowledgeMission("M1", 2_100, At(2_100));

        var request = Assert.Single(external.ListRequests());
        Assert.Equal(VirtualExternalRequestState.Succeeded, request.State);
        Assert.Equal(2, request.Attempts.Count);
        Assert.Equal(VirtualExternalRequestState.TimedOut, request.Attempts[0].State);
        Assert.Equal(VirtualExternalRequestState.Succeeded, request.Attempts[1].State);
        Assert.Equal(3_100, request.Attempts[1].VirtualOffsetMilliseconds);
        Assert.True(runtime.GetConsistency("M1", 3_100).IsConsistent);
    }

    [Fact]
    public void CanonicalStateRestore_ContinuesToIdenticalFinalState()
    {
        var state = State();
        var runtime = Runtime(state);
        runtime.DefineMission(Definition("M1"), 0, StartTimeUtc);
        runtime.DispatchMission("M1", 10, At(10));
        runtime.AdvanceMission("M1", 1_010, At(1_010));
        var checkpointJson = state.ToCanonicalJson();

        var originalState = SimulationStateStore.FromCanonicalJson(checkpointJson, EngineOptions());
        var restoredState = SimulationStateStore.FromCanonicalJson(checkpointJson, EngineOptions());
        var original = Runtime(originalState);
        var restored = Runtime(restoredState);

        original.AdvanceMission("M1", 2_010, At(2_010));
        original.AcknowledgeMission("M1", 2_100, At(2_100));
        original.AcknowledgeMission("M1", 2_200, At(2_200));
        restored.AdvanceMission("M1", 2_010, At(2_010));
        restored.AcknowledgeMission("M1", 2_100, At(2_100));
        restored.AcknowledgeMission("M1", 2_200, At(2_200));

        Assert.Equal(originalState.ToCanonicalJson(), restoredState.ToCanonicalJson());
        Assert.Equal(originalState.ComputeHash(), restoredState.ComputeHash());
        Assert.True(restored.GetConsistency("M1", 2_200).IsConsistent);
    }

    [Fact]
    public async Task ScenarioCheckpointResume_EqualsUninterruptedCompletion()
    {
        var definition = Scenario("s7-checkpoint");
        var registered = Register(definition);
        var engine = Engine();

        var uninterrupted = await engine.CreateSession(registered, definition)
            .RunToCompletionAsync();

        var firstLeg = engine.CreateSession(registered, definition);
        await firstLeg.RunUntilAsync(1_010);
        var checkpoint = firstLeg.CreateCheckpoint();
        var resumed = await engine.CreateSession(registered, definition, checkpoint)
            .RunToCompletionAsync();

        Assert.True(uninterrupted.Success);
        Assert.True(resumed.Success);
        Assert.Equal(uninterrupted.FinalStateHash, resumed.FinalStateHash);
        Assert.Equal(uninterrupted.Evidence.EvidenceHash, resumed.Evidence.EvidenceHash);
        Assert.Equal(uninterrupted.Assertions, resumed.Assertions);
    }

    [Fact]
    public async Task ScenarioReplay_IsEquivalentAcrossSpeedFactors()
    {
        var definition = Scenario("s7-replay");
        var registered = Register(definition);

        var replay = await Engine().ReplayTwiceAsync(registered, definition);

        Assert.True(replay.Equivalent);
        Assert.True(replay.First.Success);
        Assert.True(replay.Second.Success);
        Assert.Equal(replay.FirstStateHash, replay.SecondStateHash);
        Assert.Equal(replay.FirstEvidenceHash, replay.SecondEvidenceHash);
        Assert.All(replay.First.Assertions, static item => Assert.True(item.Passed));
    }

    [Fact]
    public void Runtime_EnforcesMissionCapacityBeforeProvisioningSecondMission()
    {
        var state = State();
        var options = IntegrationOptions();
        options.MaximumMissions = 1;
        var runtime = Runtime(state, options);
        runtime.DefineMission(Definition("M1"), 0, StartTimeUtc);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.DefineMission(Definition("M2", suffix: "2"), 0, StartTimeUtc));
        Assert.Single(runtime.ListMissions());
    }

    [Fact]
    public void NonContinuousRoute_IsRejectedWithoutProvisioningSubsystemState()
    {
        var state = State();
        var runtime = Runtime(state);
        var definition = Definition("M1") with
        {
            Segments =
            [
                new VirtualIntegrationSegmentDefinition("S1", "N1", "N2", 1_000, 1_000),
                new VirtualIntegrationSegmentDefinition("S2", "BROKEN", "N3", 1_000, 1_000)
            ]
        };

        Assert.Throws<InvalidOperationException>(() => runtime.DefineMission(definition, 0, StartTimeUtc));
        Assert.Empty(runtime.ListMissions());
        Assert.Empty(new VirtualPlcRuntime(state, PlcOptions()).ListBlocks());
        Assert.Empty(new VirtualRgvRuntime(state, RgvOptions()).ListSegments());
    }

    private static VirtualIntegrationRuntime CompletedMission(SimulationStateStore state)
    {
        var runtime = Runtime(state);
        runtime.DefineMission(Definition("M1"), 0, StartTimeUtc);
        runtime.DispatchMission("M1", 10, At(10));
        runtime.AdvanceMission("M1", 1_010, At(1_010));
        runtime.AdvanceMission("M1", 2_010, At(2_010));
        return runtime;
    }

    private static VirtualIntegrationMissionDefinition Definition(string missionId, string suffix = "") => new()
    {
        MissionId = missionId,
        PlcBlockKey = $"PLC{(suffix.Length == 0 ? "1" : suffix)}.DB100",
        VehicleId = $"RGV{(suffix.Length == 0 ? "1" : suffix)}",
        LoadId = $"LOAD{(suffix.Length == 0 ? "1" : suffix)}",
        SourceNodeId = $"N{(suffix.Length == 0 ? "1" : suffix + "1")}",
        DestinationNodeId = $"N{(suffix.Length == 0 ? "3" : suffix + "3")}",
        ExternalEndpointId = $"MES{(suffix.Length == 0 ? "1" : suffix)}",
        ExternalSystemKind = VirtualExternalSystemKind.Mes,
        HealthAssetId = $"ASSET{(suffix.Length == 0 ? "1" : suffix)}",
        Priority = 100,
        VehicleSpeedMillimetersPerSecond = 1_000,
        VehicleBatteryPercent = 100,
        InitialHealthScore = 95,
        InitialFusionRiskScore = 0.05,
        Segments = suffix.Length == 0
            ?
            [
                new VirtualIntegrationSegmentDefinition("S1", "N1", "N2", 1_000, 1_000),
                new VirtualIntegrationSegmentDefinition("S2", "N2", "N3", 1_000, 1_000)
            ]
            :
            [
                new VirtualIntegrationSegmentDefinition($"S{suffix}1", $"N{suffix}1", $"N{suffix}2", 1_000, 1_000),
                new VirtualIntegrationSegmentDefinition($"S{suffix}2", $"N{suffix}2", $"N{suffix}3", 1_000, 1_000)
            ]
    };

    private static SimulationScenarioEngine Engine()
    {
        var integrationOptions = IntegrationOptions();
        var actions = VirtualIntegrationScenarioHandlers.CreateActions(
            integrationOptions, PlcOptions(), RgvOptions(), TrafficOptions(), ExternalOptions(), HealthOptions());
        var assertions = VirtualIntegrationScenarioHandlers.CreateAssertions(
            integrationOptions, PlcOptions(), RgvOptions(), TrafficOptions(), ExternalOptions(), HealthOptions());
        return new SimulationScenarioEngine(actions, assertions, EngineOptions());
    }

    private static SimulationScenarioDefinition Scenario(string scenarioId) => new()
    {
        ScenarioId = scenarioId,
        Version = "1.0.0",
        Seed = 20260802,
        StartTimeUtc = StartTimeUtc,
        DurationMilliseconds = 2_300,
        StopOnAssertionFailure = true,
        Actions =
        [
            Action("define", 0, 0, "integration.mission.define", "M1", """
            {
              "PlcBlockKey":"PLC1.DB100",
              "VehicleId":"RGV1",
              "LoadId":"LOAD1",
              "SourceNodeId":"N1",
              "DestinationNodeId":"N3",
              "ExternalEndpointId":"MES1",
              "ExternalSystemKind":"Mes",
              "HealthAssetId":"ASSET1",
              "Priority":100,
              "VehicleSpeedMillimetersPerSecond":1000,
              "VehicleBatteryPercent":100,
              "InitialHealthScore":95,
              "InitialFusionRiskScore":0.05,
              "Segments":[
                {"SegmentId":"S1","FromNodeId":"N1","ToNodeId":"N2","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000},
                {"SegmentId":"S2","FromNodeId":"N2","ToNodeId":"N3","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000}
              ]
            }
            """),
            Action("dispatch", 10, 0, "integration.mission.dispatch", "M1", "{}"),
            Action("advance-1", 1_010, 0, "integration.mission.advance", "M1", "{}"),
            Action("advance-2", 2_010, 0, "integration.mission.advance", "M1", "{}"),
            Action("ack-1", 2_100, 0, "integration.mission.ack", "M1", "{}"),
            Action("ack-replay", 2_200, 0, "integration.mission.ack", "M1", "{}")
        ],
        Assertions =
        [
            Assertion("state", 2_300, 0, "integration.mission.state", "M1", "\"Acknowledged\""),
            Assertion("consistent", 2_300, 1, "integration.mission.consistent", "M1", "true"),
            Assertion("exactly-once", 2_300, 2, "integration.external.exactly-once", "M1", "true")
        ]
    };

    private static SimulationActionDefinition Action(
        string id,
        long at,
        int order,
        string kind,
        string target,
        string payload) => new()
    {
        Id = id,
        AtMilliseconds = at,
        Order = order,
        Kind = kind,
        Target = target,
        Payload = Json(payload)
    };

    private static SimulationAssertionDefinition Assertion(
        string id,
        long at,
        int order,
        string kind,
        string target,
        string expected) => new()
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
            ScenarioFile = $"{definition.ScenarioId}.json",
            ContentSha256 = SimulationScenarioValidator.ComputeSha256(content),
            CreatedAtUtc = StartTimeUtc.AddHours(-1),
            Source = "s7-integration-test",
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

    private static VirtualIntegrationRuntime Runtime(
        SimulationStateStore state,
        VirtualIntegrationOptions? options = null) =>
        new(state, options ?? IntegrationOptions(), PlcOptions(), RgvOptions(), TrafficOptions(), ExternalOptions(), HealthOptions(), 20260802);

    private static SimulationStateStore State() => new(EngineOptions());

    private static DateTimeOffset At(long offset) => StartTimeUtc.AddMilliseconds(offset);

    private static SimulationScenarioEngineOptions EngineOptions() => new()
    {
        MaximumTimelineItems = 10_000,
        MaximumStateEntries = 50_000,
        MaximumStateValueCharacters = 16_384,
        MaximumCheckpointBytes = 64 * 1024 * 1024,
        MaximumSpeedFactor = 10_000
    };

    private static VirtualIntegrationOptions IntegrationOptions() => new()
    {
        MaximumMissions = 32,
        MaximumSegmentsPerMission = 16,
        MaximumAuditRecords = 1_000,
        ReservationLeaseMilliseconds = 60_000,
        ExternalAckMaximumAttempts = 3,
        ExternalAckTimeoutMilliseconds = 100,
        ExternalAckRetryDelayMilliseconds = 1_000
    };

    private static VirtualPlcOptions PlcOptions() => new()
    {
        MaximumBlocks = 128,
        MaximumBlockBytes = 65_536,
        MaximumOperationBytes = 65_536,
        MaximumScenarioTransferBytes = 1_536,
        MaximumFaults = 1_024,
        MaximumFaultPayloadBytes = 1_536,
        MaximumAuditRecords = 2_000
    };

    private static VirtualRgvOptions RgvOptions() => new()
    {
        MaximumVehicles = 128,
        MaximumSegments = 2_048,
        MaximumRouteSegments = 256,
        MaximumAuditRecords = 2_000,
        MaximumSegmentLengthMillimeters = 10_000_000,
        MaximumSpeedMillimetersPerSecond = 20_000,
        BatteryDrainBasisPointsPerMeter = 1
    };

    private static VirtualTrafficOptions TrafficOptions() => new()
    {
        MaximumZones = 256,
        MaximumSegmentsPerZone = 16,
        MaximumReservations = 2_048,
        MaximumWaitingRequests = 2_048,
        MaximumDeadlocks = 512,
        MaximumAuditRecords = 2_000,
        MaximumRollingLookAheadSegments = 16,
        DefaultReservationLeaseMilliseconds = 60_000,
        MaximumReservationLeaseMilliseconds = 86_400_000
    };

    private static VirtualExternalOptions ExternalOptions() => new()
    {
        MaximumEndpoints = 256,
        MaximumFaults = 2_048,
        MaximumRequests = 10_000,
        MaximumAuditRecords = 2_000,
        MaximumRetryAttempts = 16,
        DefaultTimeoutMilliseconds = 5_000,
        MaximumDelayMilliseconds = 86_400_000,
        CircuitFailureThreshold = 3,
        CircuitOpenMilliseconds = 30_000
    };

    private static VirtualHealthOptions HealthOptions() => new()
    {
        MaximumAssets = 256,
        MaximumSamplesPerAsset = 2_048,
        MaximumForecastsPerAsset = 512,
        MaximumOutcomesPerAsset = 128,
        MaximumGeneratedSamplesPerAction = 1_024,
        MaximumAuditRecords = 2_000,
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
