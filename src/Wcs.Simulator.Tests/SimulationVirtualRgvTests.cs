namespace Wcs.Simulator.Tests;

using System.Text.Json;
using Wcs.Core.TransportScheduling;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualRgv;

public sealed class SimulationVirtualRgvTests
{
    private static readonly DateTimeOffset StartTimeUtc = new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Vehicle_AdvancesDeterministicallyAcrossSegments()
    {
        var runtime = Runtime();
        runtime.DefineSegment(Segment("S1", "N1", "N2", 1_000, 1_000), 0, StartTimeUtc);
        runtime.DefineSegment(Segment("S2", "N2", "N3", 2_000, 1_000), 0, StartTimeUtc);
        runtime.DefineVehicle(Vehicle("RGV1", "N1", 1_000), 0, StartTimeUtc);
        runtime.AssignRoute("RGV1", ["S1", "S2"], 0, StartTimeUtc);

        var half = runtime.AdvanceVehicle("RGV1", 500, StartTimeUtc.AddMilliseconds(500));
        Assert.Equal(500, half.DistanceMovedMillimeters);
        Assert.Equal("S1", half.Vehicle.CurrentSegmentId);
        Assert.Equal(500, half.Vehicle.SegmentProgressMillimeters);
        Assert.Equal(["RGV1"], runtime.ListOccupancy().Single().VehicleIds);

        var completed = runtime.AdvanceVehicle("RGV1", 3_000, StartTimeUtc.AddMilliseconds(3_000));
        Assert.Equal(2_500, completed.DistanceMovedMillimeters);
        Assert.Equal(["S1", "S2"], completed.CompletedSegmentIds);
        Assert.True(completed.Vehicle.RouteCompleted);
        Assert.Equal("N3", completed.Vehicle.CurrentNodeId);
        Assert.Equal(TransportVehicleOperatingState.Idle, completed.Vehicle.State);
        Assert.Empty(runtime.ListOccupancy());
    }

    [Fact]
    public void SegmentSpeedLimit_IsAppliedInsteadOfVehicleMaximum()
    {
        var runtime = Runtime();
        runtime.DefineSegment(Segment("S1", "N1", "N2", 1_000, 500), 0, StartTimeUtc);
        runtime.DefineVehicle(Vehicle("RGV1", "N1", 2_000), 0, StartTimeUtc);
        runtime.AssignRoute("RGV1", ["S1"], 0, StartTimeUtc);

        var firstSecond = runtime.AdvanceVehicle("RGV1", 1_000, StartTimeUtc.AddSeconds(1));
        Assert.Equal(500, firstSecond.Vehicle.SegmentProgressMillimeters);
        Assert.False(firstSecond.Vehicle.RouteCompleted);

        var secondSecond = runtime.AdvanceVehicle("RGV1", 2_000, StartTimeUtc.AddSeconds(2));
        Assert.True(secondSecond.Vehicle.RouteCompleted);
        Assert.Equal("N2", secondSecond.Vehicle.CurrentNodeId);
    }

    [Fact]
    public void AssignRoute_RejectsDisconnectedTopology()
    {
        var runtime = Runtime();
        runtime.DefineSegment(Segment("S1", "N1", "N2", 1_000, 1_000), 0, StartTimeUtc);
        runtime.DefineSegment(Segment("S2", "N9", "N3", 1_000, 1_000), 0, StartTimeUtc);
        runtime.DefineVehicle(Vehicle("RGV1", "N1", 1_000), 0, StartTimeUtc);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.AssignRoute("RGV1", ["S1", "S2"], 0, StartTimeUtc));
    }

    [Fact]
    public void LoadAndUnload_RequireAnIdleVehicleAtNode()
    {
        var runtime = Runtime();
        runtime.DefineSegment(Segment("S1", "N1", "N2", 1_000, 1_000), 0, StartTimeUtc);
        runtime.DefineVehicle(Vehicle("RGV1", "N1", 1_000), 0, StartTimeUtc);
        Assert.Equal("LOAD1", runtime.Load("RGV1", "LOAD1", 1, StartTimeUtc).LoadId);
        runtime.AssignRoute("RGV1", ["S1"], 2, StartTimeUtc);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.Unload("RGV1", "LOAD1", 500, StartTimeUtc.AddMilliseconds(500)));

        runtime.AdvanceVehicle("RGV1", 1_002, StartTimeUtc.AddMilliseconds(1_002));
        var unloaded = runtime.Unload("RGV1", "LOAD1", 1_003, StartTimeUtc.AddMilliseconds(1_003));
        Assert.Null(unloaded.LoadId);
        Assert.Equal("N2", unloaded.CurrentNodeId);
    }

    [Fact]
    public void TransportSnapshot_UsesExistingUnifiedVehicleContract()
    {
        var runtime = Runtime();
        runtime.DefineVehicle(new VirtualRgvVehicleDefinition
        {
            VehicleId = "RGV1",
            InitialNodeId = "N1",
            SpeedMillimetersPerSecond = 1_000,
            BatteryPercent = 88,
            Capabilities = TransportVehicleCapability.Carry | TransportVehicleCapability.Transfer
        }, 0, StartTimeUtc);

        var snapshot = runtime.GetTransportSnapshot("RGV1", StartTimeUtc);
        Assert.Equal(TransportVehicleKind.Rgv, snapshot.Kind);
        Assert.Equal(TransportVehicleOperatingState.Idle, snapshot.State);
        Assert.Equal("N1", snapshot.CurrentNodeId);
        Assert.Equal(88, snapshot.BatteryPercent);
        Assert.True(snapshot.CanAcceptTask);
    }

    [Fact]
    public void StateRestore_PreservesVehiclePositionAndAudit()
    {
        var state = State();
        var runtime = new VirtualRgvRuntime(state, Options());
        runtime.DefineSegment(Segment("S1", "N1", "N2", 1_000, 1_000), 0, StartTimeUtc);
        runtime.DefineVehicle(Vehicle("RGV1", "N1", 1_000), 0, StartTimeUtc);
        runtime.AssignRoute("RGV1", ["S1"], 0, StartTimeUtc);
        runtime.AdvanceVehicle("RGV1", 400, StartTimeUtc.AddMilliseconds(400));

        var restoredState = SimulationStateStore.FromCanonicalJson(state.ToCanonicalJson(), EngineOptions());
        var restored = new VirtualRgvRuntime(restoredState, Options());
        Assert.Equal(
            JsonSerializer.Serialize(runtime.GetVehicle("RGV1")),
            JsonSerializer.Serialize(restored.GetVehicle("RGV1")));
        Assert.Equal(runtime.ListAudit().ToArray(), restored.ListAudit().ToArray());
        Assert.Equal(runtime.GetStatus(), restored.GetStatus());
    }

    [Fact]
    public async Task ScenarioReplay_WithVirtualRgvMotion_IsEquivalent()
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
    public async Task CheckpointRestore_WithVirtualRgvMotion_MatchesContinuousRun()
    {
        var definition = Scenario();
        var registered = Register(definition);
        var engine = Engine();
        var continuous = await engine.CreateSession(registered, definition).RunToCompletionAsync();

        var interrupted = engine.CreateSession(registered, definition);
        for (var index = 0; index < 5; index++)
            Assert.True(await interrupted.StepAsync());
        interrupted.Pause();
        var checkpoint = interrupted.CreateCheckpoint();
        Assert.Contains("__vrgv.vehicle.RGV1", checkpoint.StateJson, StringComparison.Ordinal);

        var restored = engine.CreateSession(registered, definition, checkpoint);
        var result = await restored.RunToCompletionAsync();

        Assert.Equal(continuous.FinalStateHash, result.FinalStateHash);
        Assert.Equal(continuous.Evidence.EvidenceHash, result.Evidence.EvidenceHash);
    }

    [Fact]
    public void Runtime_EnforcesVehicleAndSegmentCapacity()
    {
        var options = Options();
        options.MaximumVehicles = 1;
        options.MaximumSegments = 1;
        var runtime = new VirtualRgvRuntime(State(), options);
        runtime.DefineSegment(Segment("S1", "N1", "N2", 100, 100), 0, StartTimeUtc);
        runtime.DefineVehicle(Vehicle("RGV1", "N1", 100), 0, StartTimeUtc);

        Assert.Throws<InvalidOperationException>(() =>
            runtime.DefineSegment(Segment("S2", "N2", "N3", 100, 100), 0, StartTimeUtc));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.DefineVehicle(Vehicle("RGV2", "N1", 100), 0, StartTimeUtc));
    }

    private static SimulationScenarioEngine Engine()
    {
        var options = Options();
        return new SimulationScenarioEngine(
            VirtualRgvScenarioHandlers.CreateActions(options),
            VirtualRgvScenarioHandlers.CreateAssertions(options),
            EngineOptions());
    }

    private static SimulationScenarioDefinition Scenario() => new()
    {
        ScenarioId = "virtual-rgv-motion",
        Version = "1.0.0",
        Seed = 20260730,
        StartTimeUtc = StartTimeUtc,
        DurationMilliseconds = 3_100,
        Actions =
        [
            Action("segment-1", 0, 0, "rgv.segment.define", "S1", "{\"FromNodeId\":\"N1\",\"ToNodeId\":\"N2\",\"LengthMillimeters\":1000,\"SpeedLimitMillimetersPerSecond\":1000}"),
            Action("segment-2", 0, 1, "rgv.segment.define", "S2", "{\"FromNodeId\":\"N2\",\"ToNodeId\":\"N3\",\"LengthMillimeters\":2000,\"SpeedLimitMillimetersPerSecond\":1000}"),
            Action("vehicle", 0, 2, "rgv.vehicle.define", "RGV1", "{\"InitialNodeId\":\"N1\",\"SpeedMillimetersPerSecond\":1000,\"BatteryPercent\":100,\"IsOnline\":true,\"Capabilities\":\"Carry\"}"),
            Action("route", 0, 3, "rgv.route.assign", "RGV1", "{\"SegmentIds\":[\"S1\",\"S2\"]}"),
            Action("advance-half", 500, 0, "rgv.vehicle.advance", "RGV1", "{}"),
            Action("advance-complete", 3000, 0, "rgv.vehicle.advance", "RGV1", "{}")
        ],
        Assertions =
        [
            Assertion("on-first-segment", 500, 0, "rgv.vehicle.on-segment", "RGV1", "\"S1\""),
            Assertion("first-occupied", 500, 1, "rgv.segment.occupied-by", "S1", "\"RGV1\""),
            Assertion("at-destination", 3000, 0, "rgv.vehicle.at-node", "RGV1", "\"N3\""),
            Assertion("route-complete", 3000, 1, "rgv.route.completed", "RGV1", "true"),
            Assertion("state-idle", 3000, 2, "rgv.vehicle.state", "RGV1", "\"Idle\""),
            Assertion("battery-valid", 3000, 3, "rgv.vehicle.battery.at-least", "RGV1", "99")
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
            ScenarioFile = "virtual-rgv-motion.json",
            ContentSha256 = SimulationScenarioValidator.ComputeSha256(content),
            CreatedAtUtc = StartTimeUtc.AddHours(-1),
            Source = "s3-test",
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

    private static VirtualRgvRuntime Runtime() => new(State(), Options());

    private static VirtualRgvSegmentDefinition Segment(
        string id,
        string from,
        string to,
        int length,
        int speed) => new()
    {
        SegmentId = id,
        FromNodeId = from,
        ToNodeId = to,
        LengthMillimeters = length,
        SpeedLimitMillimetersPerSecond = speed
    };

    private static VirtualRgvVehicleDefinition Vehicle(string id, string node, int speed) => new()
    {
        VehicleId = id,
        InitialNodeId = node,
        SpeedMillimetersPerSecond = speed,
        BatteryPercent = 100,
        IsOnline = true
    };

    private static SimulationStateStore State() => new(EngineOptions());

    private static SimulationScenarioEngineOptions EngineOptions() => new()
    {
        MaximumTimelineItems = 10_000,
        MaximumStateEntries = 10_000,
        MaximumStateValueCharacters = 4_096,
        MaximumCheckpointBytes = 16 * 1024 * 1024,
        MaximumSpeedFactor = 1_000
    };

    private static VirtualRgvOptions Options() => new()
    {
        MaximumVehicles = 32,
        MaximumSegments = 128,
        MaximumRouteSegments = 32,
        MaximumAuditRecords = 100,
        MaximumSegmentLengthMillimeters = 1_000_000,
        MaximumSpeedMillimetersPerSecond = 100_000,
        BatteryDrainBasisPointsPerMeter = 1
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}