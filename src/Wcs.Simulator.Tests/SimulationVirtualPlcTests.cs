namespace Wcs.Simulator.Tests;

using System.Text.Json;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualPlc;

public sealed class SimulationVirtualPlcTests
{
    private static readonly DateTimeOffset StartTimeUtc = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BlockReadWriteAndStateRestore_AreDeterministic()
    {
        var options = PlcOptions();
        var state = State();
        var plc = new VirtualPlcRuntime(state, options, deterministicSalt: 1234);

        var defined = plc.DefineBlock("PLC1.DB1", 8, [1, 2, 3, 4], 0, StartTimeUtc);
        var write = plc.Write("PLC1.DB1", 2, [9, 8, 7], 10, StartTimeUtc.AddMilliseconds(10));
        var read = plc.Read("PLC1.DB1", 0, 8, 20, StartTimeUtc.AddMilliseconds(20));

        Assert.Equal(8, defined.Size);
        Assert.True(write.Success);
        Assert.True(read.Success);
        Assert.Equal(new byte[] { 1, 2, 9, 8, 7, 0, 0, 0 }, read.Data);

        var restoredState = SimulationStateStore.FromCanonicalJson(state.ToCanonicalJson(), EngineOptions());
        var restored = new VirtualPlcRuntime(restoredState, options, deterministicSalt: 1234);
        Assert.Equal(plc.GetBlock("PLC1.DB1"), restored.GetBlock("PLC1.DB1"));
        Assert.Equal(plc.GetStatus(20), restored.GetStatus(20));
    }

    [Fact]
    public void DisconnectTimeoutAndReadWriteFailures_AreExplicitResults()
    {
        var plc = Runtime();
        plc.DefineBlock("PLC1.DB1", 4, [1, 2, 3, 4], 0, StartTimeUtc);
        plc.ApplyFault(Fault("disconnect", VirtualPlcFaultKind.Disconnect, "PLC1", 10, 20), 0, StartTimeUtc);
        plc.ApplyFault(Fault("timeout", VirtualPlcFaultKind.Timeout, "PLC1.DB1", 30, 40), 0, StartTimeUtc);
        plc.ApplyFault(Fault("read-failure", VirtualPlcFaultKind.ReadFailure, "PLC1.DB1", 50, 60), 0, StartTimeUtc);
        plc.ApplyFault(Fault("write-failure", VirtualPlcFaultKind.WriteFailure, "PLC1.DB1", 70, 80), 0, StartTimeUtc);

        var disconnected = plc.Read("PLC1.DB1", 0, 1, 15, StartTimeUtc.AddMilliseconds(15));
        var timedOut = plc.Read("PLC1.DB1", 0, 1, 35, StartTimeUtc.AddMilliseconds(35));
        var readFailure = plc.Read("PLC1.DB1", 0, 1, 55, StartTimeUtc.AddMilliseconds(55));
        var writeFailure = plc.Write("PLC1.DB1", 0, [9], 75, StartTimeUtc.AddMilliseconds(75));
        var healthy = plc.Read("PLC1.DB1", 0, 1, 90, StartTimeUtc.AddMilliseconds(90));

        Assert.Equal("Disconnected", disconnected.ErrorCode);
        Assert.True(timedOut.TimedOut);
        Assert.Equal("ReadFailure", readFailure.ErrorCode);
        Assert.Equal("WriteFailure", writeFailure.ErrorCode);
        Assert.True(healthy.Success);
        Assert.Equal(new byte[] { 1 }, healthy.Data);
    }

    [Fact]
    public void StuckBitFlipJitterAndOutOfRange_AreAppliedOnlyToReads()
    {
        var plc = Runtime(salt: 9876);
        plc.DefineBlock("PLC1.DB1", 4, [10, 20, 30, 40], 0, StartTimeUtc);
        plc.ApplyFault(Fault("stuck", VirtualPlcFaultKind.Stuck, "PLC1.DB1", 10, 100, offset: 1), 0, StartTimeUtc);
        Assert.True(plc.Write("PLC1.DB1", 1, [99], 20, StartTimeUtc.AddMilliseconds(20)).Success);
        Assert.Equal(new byte[] { 20 }, plc.Read("PLC1.DB1", 1, 1, 30, StartTimeUtc.AddMilliseconds(30)).Data);
        Assert.Equal(new byte[] { 99 }, plc.GetBlock("PLC1.DB1").Data.AsSpan(1, 1).ToArray());

        plc.ApplyFault(Fault("flip", VirtualPlcFaultKind.BitFlip, "PLC1.DB1", 110, 120, offset: 0, bitIndex: 0), 0, StartTimeUtc);
        Assert.Equal(new byte[] { 11 }, plc.Read("PLC1.DB1", 0, 1, 115, StartTimeUtc.AddMilliseconds(115)).Data);

        plc.ApplyFault(new VirtualPlcFaultDefinition
        {
            Id = "out-of-range",
            Kind = VirtualPlcFaultKind.OutOfRange,
            Target = "PLC1.DB1",
            StartMilliseconds = 130,
            EndMilliseconds = 140,
            Offset = 2,
            Length = 1,
            ReplacementBytes = [255]
        }, 0, StartTimeUtc);
        Assert.Equal(new byte[] { 255 }, plc.Read("PLC1.DB1", 2, 1, 135, StartTimeUtc.AddMilliseconds(135)).Data);

        var first = CreateJitterRead(12345);
        var second = CreateJitterRead(12345);
        Assert.Equal(first.Data, second.Data);
        Assert.Equal(first.AppliedFaultIds, second.AppliedFaultIds);
    }

    [Fact]
    public void Audit_IsBoundedAndOrderedNewestFirst()
    {
        var options = PlcOptions() withAudit(3);
        var state = State();
        var plc = new VirtualPlcRuntime(state, options);
        plc.DefineBlock("PLC1.DB1", 4, [0, 0, 0, 0], 0, StartTimeUtc);
        for (var index = 0; index < 8; index++)
            plc.Write("PLC1.DB1", 0, [(byte)index], index + 1, StartTimeUtc.AddMilliseconds(index + 1));

        var audit = plc.ListAudit(3);
        Assert.Equal(3, audit.Count);
        Assert.True(audit[0].Sequence > audit[1].Sequence);
        Assert.True(audit[1].Sequence > audit[2].Sequence);
        Assert.Equal(3, plc.GetStatus(20).AuditCount);
    }

    [Fact]
    public async Task ScenarioReplay_WithVirtualPlcFaults_IsEquivalent()
    {
        var options = PlcOptions();
        var definition = Scenario();
        var registered = Register(definition);
        var engine = new SimulationScenarioEngine(
            VirtualPlcScenarioHandlers.CreateActions(options),
            VirtualPlcScenarioHandlers.CreateAssertions(options),
            EngineOptions());

        var replay = await engine.ReplayTwiceAsync(registered, definition);

        Assert.True(replay.Equivalent);
        Assert.True(replay.First.Success);
        Assert.Equal(replay.FirstStateHash, replay.SecondStateHash);
        Assert.Equal(replay.FirstEvidenceHash, replay.SecondEvidenceHash);
        Assert.Equal(3, replay.First.Assertions.Count);
        Assert.All(replay.First.Assertions, static assertion => Assert.True(assertion.Passed));
    }

    [Fact]
    public async Task CheckpointRestore_WithVirtualPlcState_MatchesContinuousRun()
    {
        var options = PlcOptions();
        var definition = Scenario();
        var registered = Register(definition);
        var engine = new SimulationScenarioEngine(
            VirtualPlcScenarioHandlers.CreateActions(options),
            VirtualPlcScenarioHandlers.CreateAssertions(options),
            EngineOptions());

        var continuous = await engine.CreateSession(registered, definition).RunToCompletionAsync();
        var interrupted = engine.CreateSession(registered, definition);
        Assert.True(await interrupted.StepAsync());
        Assert.True(await interrupted.StepAsync());
        interrupted.Pause();
        var checkpoint = interrupted.CreateCheckpoint();
        Assert.Contains("__vplc.block.PLC1.DB1", checkpoint.StateJson, StringComparison.Ordinal);

        var restored = engine.CreateSession(registered, definition, checkpoint);
        var result = await restored.RunToCompletionAsync();

        Assert.Equal(continuous.FinalStateHash, result.FinalStateHash);
        Assert.Equal(continuous.Evidence.EvidenceHash, result.Evidence.EvidenceHash);
    }

    private static VirtualPlcOperationResult CreateJitterRead(ulong salt)
    {
        var plc = Runtime(salt);
        plc.DefineBlock("PLC1.DB1", 4, [100, 100, 100, 100], 0, StartTimeUtc);
        plc.ApplyFault(new VirtualPlcFaultDefinition
        {
            Id = "jitter",
            Kind = VirtualPlcFaultKind.Jitter,
            Target = "PLC1.DB1",
            StartMilliseconds = 1,
            EndMilliseconds = 100,
            Offset = 0,
            Length = 4,
            JitterMinimum = -5,
            JitterMaximum = 5
        }, 0, StartTimeUtc);
        return plc.Read("PLC1.DB1", 0, 4, 10, StartTimeUtc.AddMilliseconds(10));
    }

    private static VirtualPlcFaultDefinition Fault(
        string id,
        VirtualPlcFaultKind kind,
        string target,
        long start,
        long end,
        int offset = 0,
        int bitIndex = 0) => new()
    {
        Id = id,
        Kind = kind,
        Target = target,
        StartMilliseconds = start,
        EndMilliseconds = end,
        Offset = offset,
        Length = 1,
        BitIndex = bitIndex
    };

    private static SimulationScenarioDefinition Scenario() => new()
    {
        ScenarioId = "virtual-plc-faults",
        Version = "1.0.0",
        Seed = 20260729,
        StartTimeUtc = StartTimeUtc,
        DurationMilliseconds = 100,
        Actions =
        [
            Action("define", 0, "plc.block.define", "PLC1.DB1", "{\"Size\":4,\"InitialBase64\":\"AQIDBA==\"}"),
            Action("apply-flip", 10, "plc.fault.apply", "PLC1.DB1", "{\"Id\":\"flip\",\"Kind\":\"BitFlip\",\"StartMilliseconds\":10,\"EndMilliseconds\":30,\"Offset\":0,\"Length\":1,\"BitIndex\":0}"),
            Action("read-flipped", 20, "plc.block.read", "PLC1.DB1", "{\"Offset\":0,\"Count\":1,\"ResultStateKey\":\"read.flip\"}"),
            Action("clear-flip", 40, "plc.fault.clear", "flip", "{}")
        ],
        Assertions =
        [
            Assertion("fault-is-active", 15, "plc.fault.active", "flip", "true"),
            Assertion("base-memory-unchanged", 25, "plc.block.equals", "PLC1.DB1", "{\"Offset\":0,\"DataBase64\":\"AQ==\"}"),
            Assertion("fault-is-cleared", 50, "plc.fault.active", "flip", "false")
        ]
    };

    private static SimulationActionDefinition Action(
        string id,
        long at,
        string kind,
        string target,
        string payload) => new()
    {
        Id = id,
        AtMilliseconds = at,
        Kind = kind,
        Target = target,
        Payload = Json(payload)
    };

    private static SimulationAssertionDefinition Assertion(
        string id,
        long at,
        string kind,
        string target,
        string expected) => new()
    {
        Id = id,
        AtMilliseconds = at,
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
            ScenarioFile = "virtual-plc-faults.json",
            ContentSha256 = SimulationScenarioValidator.ComputeSha256(content),
            CreatedAtUtc = StartTimeUtc.AddHours(-1),
            Source = "s2-test",
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

    private static VirtualPlcRuntime Runtime(ulong salt = 0) =>
        new(State(), PlcOptions(), salt);

    private static SimulationStateStore State() => new(EngineOptions());

    private static SimulationScenarioEngineOptions EngineOptions() => new()
    {
        MaximumTimelineItems = 10_000,
        MaximumStateEntries = 10_000,
        MaximumStateValueCharacters = 4_096,
        MaximumCheckpointBytes = 16 * 1024 * 1024,
        MaximumSpeedFactor = 1_000
    };

    private static VirtualPlcOptions PlcOptions() => new()
    {
        MaximumBlocks = 32,
        MaximumBlockBytes = 65_536,
        MaximumOperationBytes = 65_536,
        MaximumScenarioTransferBytes = 1_536,
        MaximumFaults = 128,
        MaximumFaultPayloadBytes = 1_536,
        MaximumAuditRecords = 100
    };

    private static VirtualPlcOptions withAudit(this VirtualPlcOptions source, int maximumAuditRecords) => new()
    {
        MaximumBlocks = source.MaximumBlocks,
        MaximumBlockBytes = source.MaximumBlockBytes,
        MaximumOperationBytes = source.MaximumOperationBytes,
        MaximumScenarioTransferBytes = source.MaximumScenarioTransferBytes,
        MaximumFaults = source.MaximumFaults,
        MaximumFaultPayloadBytes = source.MaximumFaultPayloadBytes,
        MaximumAuditRecords = maximumAuditRecords
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
