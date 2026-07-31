namespace Wcs.Simulator.Tests;

using System.Text.Json;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

public sealed class SimulationVirtualTrafficTests
{
    private static readonly DateTimeOffset StartTimeUtc = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReservationConflict_CreatesDeterministicWaitEdge()
    {
        var (traffic, _) = Runtime();
        PrepareTwoZoneVehicles(traffic);

        Assert.True(traffic.RequestReservation("RGV1", "S1", 10, 1_000, 0, StartTimeUtc).Granted);
        var decision = traffic.RequestReservation("RGV2", "S1", 20, 1_000, 1, StartTimeUtc.AddMilliseconds(1));

        Assert.False(decision.Granted);
        Assert.Equal(["RGV1"], decision.BlockingVehicleIds);
        var request = Assert.Single(traffic.ListWaitingRequests());
        Assert.Equal("RGV2", request.VehicleId);
        Assert.Equal("S1", request.SegmentId);
        var edge = Assert.Single(traffic.ListWaitEdges());
        Assert.Equal("RGV2", edge.WaitingVehicleId);
        Assert.Equal("RGV1", edge.BlockingVehicleId);
        Assert.Equal("Z1", edge.ZoneId);
    }

    [Fact]
    public void LeaseExpiry_GrantsOldestWaitingRequest()
    {
        var (traffic, _) = Runtime();
        PrepareTwoZoneVehicles(traffic);

        traffic.RequestReservation("RGV1", "S1", 10, 100, 0, StartTimeUtc);
        traffic.RequestReservation("RGV2", "S1", 20, 500, 1, StartTimeUtc.AddMilliseconds(1));

        Assert.Equal(1, traffic.ExpireReservations(100, StartTimeUtc.AddMilliseconds(100)));
        Assert.Empty(traffic.ListWaitingRequests());
        var active = Assert.Single(traffic.ListReservations(true, 100));
        Assert.Equal("RGV2", active.VehicleId);
        Assert.Equal(VirtualTrafficReservationState.Granted, active.State);
    }

    [Fact]
    public void TwoVehicleCycle_DetectsAndResolvesWithDeterministicVictim()
    {
        var (traffic, _) = Runtime();
        PrepareTwoZoneVehicles(traffic);

        traffic.RequestReservation("RGV1", "S1", 10, 10_000, 0, StartTimeUtc);
        traffic.RequestReservation("RGV2", "S2", 20, 10_000, 0, StartTimeUtc);
        traffic.RequestReservation("RGV1", "S2", 10, 10_000, 10, StartTimeUtc.AddMilliseconds(10));
        traffic.RequestReservation("RGV2", "S1", 20, 10_000, 10, StartTimeUtc.AddMilliseconds(10));

        var deadlock = Assert.Single(traffic.DetectDeadlocks(20, StartTimeUtc.AddMilliseconds(20)));
        Assert.Equal(["RGV1", "RGV2"], deadlock.VehicleIds);
        Assert.Equal("RGV2", deadlock.VictimVehicleId);
        Assert.Equal(2, deadlock.Edges.Count);
        Assert.False(deadlock.Resolved);

        var resolution = traffic.ResolveDeadlock(deadlock.DeadlockId, 30, StartTimeUtc.AddMilliseconds(30));
        Assert.Equal("RGV2", resolution.VictimVehicleId);
        Assert.Single(resolution.ReleasedReservationIds);
        Assert.Single(resolution.CancelledRequestIds);
        Assert.Single(resolution.NewlyGrantedRequestIds);
        Assert.True(resolution.Deadlock.Resolved);
        Assert.Empty(traffic.ListDeadlocks());
        Assert.Empty(traffic.ListWaitEdges());

        var active = traffic.ListReservations(true, 30);
        Assert.Contains(active, item => item.VehicleId == "RGV1" && item.ZoneId == "Z2");
        Assert.DoesNotContain(active, item => item.VehicleId == "RGV2");
    }

    [Fact]
    public void ThreeVehicleCycle_UsesPriorityThenSequenceForVictim()
    {
        var engineOptions = EngineOptions();
        var state = new SimulationStateStore(engineOptions);
        var rgv = new VirtualRgvRuntime(state, RgvOptions());
        DefineSegment(rgv, "S1", "N1", "N2");
        DefineSegment(rgv, "S2", "N2", "N3");
        DefineSegment(rgv, "S3", "N3", "N1");
        DefineVehicle(rgv, "RGV1", "N1");
        DefineVehicle(rgv, "RGV2", "N2");
        DefineVehicle(rgv, "RGV3", "N3");
        var traffic = new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions());
        DefineZone(traffic, "Z1", "S1");
        DefineZone(traffic, "Z2", "S2");
        DefineZone(traffic, "Z3", "S3");

        traffic.RequestReservation("RGV1", "S1", 10, 10_000, 0, StartTimeUtc);
        traffic.RequestReservation("RGV2", "S2", 30, 10_000, 0, StartTimeUtc);
        traffic.RequestReservation("RGV3", "S3", 20, 10_000, 0, StartTimeUtc);
        traffic.RequestReservation("RGV1", "S2", 10, 10_000, 10, StartTimeUtc);
        traffic.RequestReservation("RGV2", "S3", 30, 10_000, 11, StartTimeUtc);
        traffic.RequestReservation("RGV3", "S1", 20, 10_000, 12, StartTimeUtc);

        var deadlock = Assert.Single(traffic.DetectDeadlocks(20, StartTimeUtc));
        Assert.Equal(["RGV1", "RGV2", "RGV3"], deadlock.VehicleIds);
        Assert.Equal("RGV2", deadlock.VictimVehicleId);
    }

    [Fact]
    public void RollingReservation_UsesCurrentRouteWindowAndReleasesPassedSegments()
    {
        var engineOptions = EngineOptions();
        var state = new SimulationStateStore(engineOptions);
        var rgv = new VirtualRgvRuntime(state, RgvOptions());
        DefineSegment(rgv, "S1", "N1", "N2");
        DefineSegment(rgv, "S2", "N2", "N3");
        DefineVehicle(rgv, "RGV1", "N1");
        rgv.AssignRoute("RGV1", ["S1", "S2"], 0, StartTimeUtc);
        var traffic = new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions());
        DefineZone(traffic, "Z1", "S1");
        DefineZone(traffic, "Z2", "S2");

        var rolling = traffic.ReserveRollingWindow("RGV1", 2, 10, 10_000, 0, StartTimeUtc);
        Assert.True(rolling.AllGranted);
        Assert.Equal(2, traffic.ListReservations(true, 0).Count);

        rgv.AdvanceVehicle("RGV1", 1_000, StartTimeUtc.AddMilliseconds(1_000));
        var released = traffic.ReleasePassedReservations("RGV1", 1_000, StartTimeUtc.AddMilliseconds(1_000));
        Assert.Single(released);
        var active = Assert.Single(traffic.ListReservations(true, 1_000));
        Assert.Equal("S2", active.SegmentId);
    }

    [Fact]
    public void StateRestore_PreservesReservationsWaitGraphDeadlockAndAudit()
    {
        var engineOptions = EngineOptions();
        var state = new SimulationStateStore(engineOptions);
        var traffic = new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions());
        PrepareTwoZoneVehicles(traffic);
        traffic.RequestReservation("RGV1", "S1", 10, 10_000, 0, StartTimeUtc);
        traffic.RequestReservation("RGV2", "S2", 20, 10_000, 0, StartTimeUtc);
        traffic.RequestReservation("RGV1", "S2", 10, 10_000, 10, StartTimeUtc);
        traffic.RequestReservation("RGV2", "S1", 20, 10_000, 10, StartTimeUtc);
        traffic.DetectDeadlocks(20, StartTimeUtc);

        var canonical = state.ToCanonicalJson();
        var restoredState = SimulationStateStore.FromCanonicalJson(canonical, engineOptions);
        var restored = new VirtualTrafficRuntime(restoredState, TrafficOptions(), RgvOptions());

        Assert.Equal(canonical, restoredState.ToCanonicalJson());
        Assert.Equal(JsonSerializer.Serialize(traffic.ListZones()), JsonSerializer.Serialize(restored.ListZones()));
        Assert.Equal(JsonSerializer.Serialize(traffic.ListReservations(false, 20)), JsonSerializer.Serialize(restored.ListReservations(false, 20)));
        Assert.Equal(JsonSerializer.Serialize(traffic.ListWaitingRequests(false)), JsonSerializer.Serialize(restored.ListWaitingRequests(false)));
        Assert.Equal(JsonSerializer.Serialize(traffic.ListWaitEdges()), JsonSerializer.Serialize(restored.ListWaitEdges()));
        Assert.Equal(JsonSerializer.Serialize(traffic.ListDeadlocks(false)), JsonSerializer.Serialize(restored.ListDeadlocks(false)));
        Assert.Equal(JsonSerializer.Serialize(traffic.ListAudit()), JsonSerializer.Serialize(restored.ListAudit()));
        Assert.Equal(state.ComputeHash(), restoredState.ComputeHash());
    }

    [Fact]
    public async Task ScenarioReplay_WithDeadlockResolution_IsEquivalent()
    {
        var definition = Scenario();
        var registered = Register(definition);
        var engine = Engine();

        var replay = await engine.ReplayTwiceAsync(registered, definition);

        Assert.True(replay.Equivalent);
        Assert.True(replay.First.Success);
        Assert.Equal(replay.FirstStateHash, replay.SecondStateHash);
        Assert.Equal(replay.FirstEvidenceHash, replay.SecondEvidenceHash);
        Assert.All(replay.First.Assertions, static assertion => Assert.True(assertion.Passed));
    }

    [Fact]
    public async Task CheckpointRestore_WithWaitGraph_MatchesContinuousRun()
    {
        var definition = Scenario();
        var registered = Register(definition);
        var engine = Engine();
        var continuous = await engine.CreateSession(registered, definition).RunToCompletionAsync();

        var interrupted = engine.CreateSession(registered, definition);
        for (var index = 0; index < 10; index++)
            Assert.True(await interrupted.StepAsync());
        interrupted.Pause();
        var checkpoint = interrupted.CreateCheckpoint();
        Assert.Contains("__vtraffic.request", checkpoint.StateJson, StringComparison.Ordinal);

        var restored = engine.CreateSession(registered, definition, checkpoint);
        var result = await restored.RunToCompletionAsync();

        Assert.Equal(continuous.FinalStateHash, result.FinalStateHash);
        Assert.Equal(continuous.Evidence.EvidenceHash, result.Evidence.EvidenceHash);
    }

    [Fact]
    public void Runtime_EnforcesZoneAndWaitingRequestCapacity()
    {
        var trafficOptions = TrafficOptions();
        trafficOptions.MaximumZones = 1;
        trafficOptions.MaximumReservations = 1;
        trafficOptions.MaximumWaitingRequests = 1;
        var state = new SimulationStateStore(EngineOptions());
        var rgv = new VirtualRgvRuntime(state, RgvOptions());
        DefineSegment(rgv, "S1", "N1", "N2");
        DefineSegment(rgv, "S2", "N2", "N3");
        DefineVehicle(rgv, "RGV1", "N1");
        DefineVehicle(rgv, "RGV2", "N2");
        DefineVehicle(rgv, "RGV3", "N3");
        var traffic = new VirtualTrafficRuntime(state, trafficOptions, RgvOptions());
        DefineZone(traffic, "Z1", "S1");

        Assert.Throws<InvalidOperationException>(() => DefineZone(traffic, "Z2", "S2"));
        Assert.True(traffic.RequestReservation("RGV1", "S1", 10, 10_000, 0, StartTimeUtc).Granted);
        Assert.False(traffic.RequestReservation("RGV2", "S1", 20, 10_000, 1, StartTimeUtc).Granted);
        Assert.Throws<InvalidOperationException>(() =>
            traffic.RequestReservation("RGV3", "S1", 30, 10_000, 2, StartTimeUtc));
    }

    private static (VirtualTrafficRuntime Traffic, SimulationStateStore State) Runtime()
    {
        var state = new SimulationStateStore(EngineOptions());
        return (new VirtualTrafficRuntime(state, TrafficOptions(), RgvOptions()), state);
    }

    private static void PrepareTwoZoneVehicles(VirtualTrafficRuntime traffic)
    {
        var stateField = typeof(VirtualTrafficRuntime)
            .GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var state = (SimulationStateStore)stateField.GetValue(traffic)!;
        var rgv = new VirtualRgvRuntime(state, RgvOptions());
        DefineSegment(rgv, "S1", "N1", "N2");
        DefineSegment(rgv, "S2", "N2", "N1");
        DefineVehicle(rgv, "RGV1", "N1");
        DefineVehicle(rgv, "RGV2", "N2");
        DefineZone(traffic, "Z1", "S1");
        DefineZone(traffic, "Z2", "S2");
    }

    private static void DefineSegment(VirtualRgvRuntime rgv, string id, string from, string to) =>
        rgv.DefineSegment(new VirtualRgvSegmentDefinition
        {
            SegmentId = id,
            FromNodeId = from,
            ToNodeId = to,
            LengthMillimeters = 1_000,
            SpeedLimitMillimetersPerSecond = 1_000
        }, 0, StartTimeUtc);

    private static void DefineVehicle(VirtualRgvRuntime rgv, string id, string node) =>
        rgv.DefineVehicle(new VirtualRgvVehicleDefinition
        {
            VehicleId = id,
            InitialNodeId = node,
            SpeedMillimetersPerSecond = 1_000,
            BatteryPercent = 100,
            IsOnline = true
        }, 0, StartTimeUtc);

    private static void DefineZone(VirtualTrafficRuntime traffic, string id, params string[] segments) =>
        traffic.DefineZone(new VirtualTrafficZoneDefinition
        {
            ZoneId = id,
            SegmentIds = segments,
            Capacity = 1,
            Kind = VirtualTrafficZoneKind.SharedSegment
        }, 0, StartTimeUtc);

    private static SimulationScenarioEngine Engine()
    {
        var rgvOptions = RgvOptions();
        var trafficOptions = TrafficOptions();
        return new SimulationScenarioEngine(
            VirtualRgvScenarioHandlers.CreateActions(rgvOptions)
                .Concat(VirtualTrafficScenarioHandlers.CreateActions(trafficOptions, rgvOptions)),
            VirtualRgvScenarioHandlers.CreateAssertions(rgvOptions)
                .Concat(VirtualTrafficScenarioHandlers.CreateAssertions(trafficOptions, rgvOptions)),
            EngineOptions());
    }

    private static SimulationScenarioDefinition Scenario() => new()
    {
        ScenarioId = "virtual-traffic-deadlock",
        Version = "1.0.0",
        Seed = 20260731,
        StartTimeUtc = StartTimeUtc,
        DurationMilliseconds = 100,
        Actions =
        [
            Action("segment-1", 0, 0, "rgv.segment.define", "S1", "{\"FromNodeId\":\"N1\",\"ToNodeId\":\"N2\",\"LengthMillimeters\":1000,\"SpeedLimitMillimetersPerSecond\":1000}"),
            Action("segment-2", 0, 1, "rgv.segment.define", "S2", "{\"FromNodeId\":\"N2\",\"ToNodeId\":\"N1\",\"LengthMillimeters\":1000,\"SpeedLimitMillimetersPerSecond\":1000}"),
            Action("vehicle-1", 0, 2, "rgv.vehicle.define", "RGV1", "{\"InitialNodeId\":\"N1\",\"SpeedMillimetersPerSecond\":1000}"),
            Action("vehicle-2", 0, 3, "rgv.vehicle.define", "RGV2", "{\"InitialNodeId\":\"N2\",\"SpeedMillimetersPerSecond\":1000}"),
            Action("zone-1", 0, 4, "traffic.zone.define", "Z1", "{\"SegmentIds\":[\"S1\"],\"Capacity\":1,\"Kind\":\"SharedSegment\"}"),
            Action("zone-2", 0, 5, "traffic.zone.define", "Z2", "{\"SegmentIds\":[\"S2\"],\"Capacity\":1,\"Kind\":\"OpposingDirection\"}"),
            Action("hold-1", 0, 6, "traffic.reserve", "RGV1", "{\"SegmentId\":\"S1\",\"Priority\":10,\"LeaseMilliseconds\":10000}"),
            Action("hold-2", 0, 7, "traffic.reserve", "RGV2", "{\"SegmentId\":\"S2\",\"Priority\":20,\"LeaseMilliseconds\":10000}"),
            Action("wait-1", 10, 0, "traffic.reserve", "RGV1", "{\"SegmentId\":\"S2\",\"Priority\":10,\"LeaseMilliseconds\":10000}"),
            Action("wait-2", 10, 1, "traffic.reserve", "RGV2", "{\"SegmentId\":\"S1\",\"Priority\":20,\"LeaseMilliseconds\":10000}"),
            Action("detect", 20, 0, "traffic.deadlock.detect", "all", "{}"),
            Action("resolve", 30, 0, "traffic.deadlock.resolve", "DL-000000000007", "{}")
        ],
        Assertions =
        [
            Assertion("wait-edge-1", 10, 2, "traffic.waits-for", "RGV1", "\"RGV2\""),
            Assertion("wait-edge-2", 10, 3, "traffic.waits-for", "RGV2", "\"RGV1\""),
            Assertion("deadlock-found", 20, 1, "traffic.deadlock.exists", "all", "true"),
            Assertion("victim", 20, 2, "traffic.deadlock.victim", "DL-000000000007", "\"RGV2\""),
            Assertion("deadlock-cleared", 30, 1, "traffic.deadlock.exists", "all", "false"),
            Assertion("zone-2-owner", 30, 2, "traffic.reservation.owned-by", "S2", "\"RGV1\"")
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
            ScenarioFile = "virtual-traffic-deadlock.json",
            ContentSha256 = SimulationScenarioValidator.ComputeSha256(content),
            CreatedAtUtc = StartTimeUtc.AddHours(-1),
            Source = "s4-test",
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
        MaximumStateEntries = 20_000,
        MaximumStateValueCharacters = 4_096,
        MaximumCheckpointBytes = 16 * 1024 * 1024,
        MaximumSpeedFactor = 1_000
    };

    private static VirtualRgvOptions RgvOptions() => new()
    {
        MaximumVehicles = 32,
        MaximumSegments = 128,
        MaximumRouteSegments = 32,
        MaximumAuditRecords = 100,
        MaximumSegmentLengthMillimeters = 1_000_000,
        MaximumSpeedMillimetersPerSecond = 100_000,
        BatteryDrainBasisPointsPerMeter = 1
    };

    private static VirtualTrafficOptions TrafficOptions() => new()
    {
        MaximumZones = 32,
        MaximumSegmentsPerZone = 8,
        MaximumReservations = 128,
        MaximumWaitingRequests = 128,
        MaximumDeadlocks = 32,
        MaximumAuditRecords = 100,
        MaximumRollingLookAheadSegments = 8,
        DefaultReservationLeaseMilliseconds = 1_000,
        MaximumReservationLeaseMilliseconds = 100_000
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
