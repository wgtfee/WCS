namespace Wcs.Simulator.Tests;

using System.Text.Json;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;

public sealed class SimulationVirtualExternalTests
{
    private static readonly DateTimeOffset StartTimeUtc = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private const string PayloadHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void TransientTimeout_RetriesDeterministicallyAndRecovers()
    {
        var (runtime, _) = Runtime();
        Define(runtime, "MES1", VirtualExternalSystemKind.Mes);
        runtime.ApplyFault(new VirtualExternalFaultDefinition(
            "F1", "MES1", VirtualExternalFaultKind.Timeout, 0, 50), 0, StartTimeUtc);

        var result = runtime.Invoke(new VirtualExternalInvokeRequest(
            "MES1", "Order.Push", "idem-1", PayloadHash,
            MaxAttempts: 2, TimeoutMilliseconds: 20, RetryDelayMilliseconds: 60),
            0, StartTimeUtc);

        Assert.Equal(VirtualExternalRequestState.Succeeded, result.State);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(VirtualExternalRequestState.TimedOut, result.Attempts[0].State);
        Assert.Equal(VirtualExternalRequestState.Succeeded, result.Attempts[1].State);
        Assert.Equal(60, result.Attempts[1].VirtualOffsetMilliseconds);
        Assert.Equal(VirtualExternalCircuitState.Closed, runtime.GetEndpoint("MES1", 60).CircuitState);
    }

    [Fact]
    public void RepeatedFailures_OpenCircuit_AndHalfOpenSuccessClosesIt()
    {
        var options = Options();
        options.CircuitFailureThreshold = 3;
        options.CircuitOpenMilliseconds = 100;
        var state = new SimulationStateStore(EngineOptions());
        var runtime = new VirtualExternalRuntime(state, options);
        Define(runtime, "SQL1", VirtualExternalSystemKind.SqlServer);
        runtime.ApplyFault(new VirtualExternalFaultDefinition(
            "F1", "SQL1", VirtualExternalFaultKind.Unavailable, 0, 50), 0, StartTimeUtc);

        var failed = runtime.Invoke(new VirtualExternalInvokeRequest(
            "SQL1", "Write.Batch", "idem-2", PayloadHash,
            MaxAttempts: 3, TimeoutMilliseconds: 10, RetryDelayMilliseconds: 0),
            0, StartTimeUtc);

        Assert.Equal(VirtualExternalRequestState.Failed, failed.State);
        var open = runtime.GetEndpoint("SQL1", 0);
        Assert.Equal(VirtualExternalCircuitState.Open, open.CircuitState);
        Assert.Equal(3, open.ConsecutiveFailures);
        Assert.Equal(100, open.CircuitOpenUntilOffsetMilliseconds);

        var rejected = runtime.Invoke(new VirtualExternalInvokeRequest(
            "SQL1", "Write.Batch", "idem-3", PayloadHash), 10, StartTimeUtc.AddMilliseconds(10));
        Assert.Equal(VirtualExternalRequestState.RejectedByCircuit, rejected.State);

        var recovered = runtime.Invoke(new VirtualExternalInvokeRequest(
            "SQL1", "Write.Batch", "idem-4", PayloadHash), 100, StartTimeUtc.AddMilliseconds(100));
        Assert.Equal(VirtualExternalRequestState.Succeeded, recovered.State);
        Assert.Equal(VirtualExternalCircuitState.Closed, runtime.GetEndpoint("SQL1", 100).CircuitState);
        Assert.Equal(0, runtime.GetEndpoint("SQL1", 100).ConsecutiveFailures);
    }

    [Fact]
    public void SuccessfulIdempotencyKey_ReplaysWithoutCreatingSecondRequest()
    {
        var (runtime, _) = Runtime();
        Define(runtime, "MES1", VirtualExternalSystemKind.Mes);

        var first = runtime.Invoke(new VirtualExternalInvokeRequest(
            "MES1", "Order.Push", "same-key", PayloadHash), 0, StartTimeUtc);
        var second = runtime.Invoke(new VirtualExternalInvokeRequest(
            "MES1", "Order.Push", "same-key", PayloadHash), 10, StartTimeUtc.AddMilliseconds(10));

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.True(second.IdempotencyReplayed);
        Assert.Single(runtime.ListRequests());
        Assert.Contains(runtime.ListAudit(), item => item.Operation == "request.idempotent-replay");
    }

    [Fact]
    public void OverlappingFaultWindows_AreRejectedForOneEndpoint()
    {
        var (runtime, _) = Runtime();
        Define(runtime, "NET1", VirtualExternalSystemKind.Network);
        runtime.ApplyFault(new VirtualExternalFaultDefinition(
            "F1", "NET1", VirtualExternalFaultKind.PacketLoss, 0, 100), 0, StartTimeUtc);

        Assert.Throws<InvalidOperationException>(() => runtime.ApplyFault(
            new VirtualExternalFaultDefinition(
                "F2", "NET1", VirtualExternalFaultKind.ConnectionReset, 50, 150),
            0, StartTimeUtc));
    }

    [Fact]
    public void StateRestore_PreservesEndpointsFaultsRequestsCircuitsAndAudit()
    {
        var options = Options();
        var state = new SimulationStateStore(EngineOptions());
        var runtime = new VirtualExternalRuntime(state, options);
        Define(runtime, "MES1", VirtualExternalSystemKind.Mes);
        runtime.ApplyFault(new VirtualExternalFaultDefinition(
            "F1", "MES1", VirtualExternalFaultKind.HttpStatus, 0, 100,
            HttpStatusCode: 503), 0, StartTimeUtc);
        runtime.Invoke(new VirtualExternalInvokeRequest(
            "MES1", "Order.Push", "idem-5", PayloadHash), 0, StartTimeUtc);

        var canonical = state.ToCanonicalJson();
        var restoredState = SimulationStateStore.FromCanonicalJson(canonical, EngineOptions());
        var restored = new VirtualExternalRuntime(restoredState, options);

        Assert.Equal(canonical, restoredState.ToCanonicalJson());
        Assert.Equal(JsonSerializer.Serialize(runtime.ListEndpoints()), JsonSerializer.Serialize(restored.ListEndpoints()));
        Assert.Equal(JsonSerializer.Serialize(runtime.ListFaults()), JsonSerializer.Serialize(restored.ListFaults()));
        Assert.Equal(JsonSerializer.Serialize(runtime.ListRequests()), JsonSerializer.Serialize(restored.ListRequests()));
        Assert.Equal(JsonSerializer.Serialize(runtime.ListAudit()), JsonSerializer.Serialize(restored.ListAudit()));
        Assert.Equal(state.ComputeHash(), restoredState.ComputeHash());
    }

    [Fact]
    public async Task ScenarioReplay_WithTransientExternalFailure_IsEquivalent()
    {
        var definition = Scenario();
        var registered = Register(definition);
        var engine = new SimulationScenarioEngine(
            VirtualExternalScenarioHandlers.CreateActions(Options()),
            VirtualExternalScenarioHandlers.CreateAssertions(Options()),
            EngineOptions());

        var replay = await engine.ReplayTwiceAsync(registered, definition);

        Assert.True(replay.Equivalent);
        Assert.True(replay.First.Success);
        Assert.Equal(replay.FirstStateHash, replay.SecondStateHash);
        Assert.Equal(replay.FirstEvidenceHash, replay.SecondEvidenceHash);
        Assert.All(replay.First.Assertions, static assertion => Assert.True(assertion.Passed));
    }

    [Fact]
    public void Runtime_EnforcesEndpointFaultAndRequestCapacity()
    {
        var options = Options();
        options.MaximumEndpoints = 1;
        options.MaximumFaults = 1;
        options.MaximumRequests = 1;
        var runtime = new VirtualExternalRuntime(new SimulationStateStore(EngineOptions()), options);
        Define(runtime, "MES1", VirtualExternalSystemKind.Mes);

        Assert.Throws<InvalidOperationException>(() => Define(runtime, "SQL1", VirtualExternalSystemKind.SqlServer));
        runtime.ApplyFault(new VirtualExternalFaultDefinition(
            "F1", "MES1", VirtualExternalFaultKind.Unavailable, 0, 10), 0, StartTimeUtc);
        Assert.Throws<InvalidOperationException>(() => runtime.ApplyFault(
            new VirtualExternalFaultDefinition(
                "F2", "MES1", VirtualExternalFaultKind.Unavailable, 20, 30), 0, StartTimeUtc));

        runtime.Invoke(new VirtualExternalInvokeRequest(
            "MES1", "Order.Push", "idem-6", PayloadHash), 20, StartTimeUtc.AddMilliseconds(20));
        Assert.Throws<InvalidOperationException>(() => runtime.Invoke(
            new VirtualExternalInvokeRequest(
                "MES1", "Order.Push", "idem-7", PayloadHash), 30, StartTimeUtc.AddMilliseconds(30)));
    }

    private static (VirtualExternalRuntime Runtime, SimulationStateStore State) Runtime()
    {
        var state = new SimulationStateStore(EngineOptions());
        return (new VirtualExternalRuntime(state, Options()), state);
    }

    private static void Define(
        VirtualExternalRuntime runtime,
        string endpointId,
        VirtualExternalSystemKind kind) =>
        runtime.DefineEndpoint(new VirtualExternalEndpointDefinition(endpointId, kind), 0, StartTimeUtc);

    private static SimulationScenarioDefinition Scenario() => new()
    {
        ScenarioId = "virtual-external-transient",
        Version = "1.0.0",
        Seed = 20260801,
        StartTimeUtc = StartTimeUtc,
        DurationMilliseconds = 100,
        Actions =
        [
            Action("endpoint", 0, 0, "external.endpoint.define", "MES1", "{\"Kind\":\"Mes\"}"),
            Action("fault", 0, 1, "external.fault.apply", "F1", "{\"EndpointId\":\"MES1\",\"Kind\":\"Timeout\",\"StartsAtOffsetMilliseconds\":0,\"EndsAtOffsetMilliseconds\":50}"),
            Action("invoke", 0, 2, "external.request.invoke", "MES1", "{\"Operation\":\"Order.Push\",\"IdempotencyKey\":\"scenario-key\",\"PayloadHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"MaxAttempts\":2,\"TimeoutMilliseconds\":20,\"RetryDelayMilliseconds\":60}")
        ],
        Assertions =
        [
            Assertion("request-state", 70, 0, "external.request.state", "EXTREQ-000000000001", "\"Succeeded\""),
            Assertion("attempts", 70, 1, "external.request.attempts", "EXTREQ-000000000001", "2"),
            Assertion("circuit", 70, 2, "external.circuit.state", "MES1", "\"Closed\""),
            Assertion("fault-ended", 70, 3, "external.fault.active", "F1", "false")
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
            ScenarioFile = "virtual-external-transient.json",
            ContentSha256 = SimulationScenarioValidator.ComputeSha256(content),
            CreatedAtUtc = StartTimeUtc.AddHours(-1),
            Source = "s5-test",
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

    private static VirtualExternalOptions Options() => new()
    {
        MaximumEndpoints = 32,
        MaximumFaults = 128,
        MaximumRequests = 512,
        MaximumAuditRecords = 100,
        MaximumRetryAttempts = 8,
        DefaultTimeoutMilliseconds = 100,
        MaximumDelayMilliseconds = 100_000,
        CircuitFailureThreshold = 3,
        CircuitOpenMilliseconds = 1_000
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}