namespace Wcs.Simulator.Tests;

using System.Text;
using System.Text.Json;
using Wcs.Simulator.Governance;
using Wcs.Simulator.ScenarioEngine;

public sealed class SimulationScenarioEngineTests
{
    private static readonly DateTimeOffset StartTimeUtc = new(2026, 7, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StrictJsonParser_OrdersActionsBeforeAssertionsDeterministically()
    {
        var json = """
        {
          "SchemaVersion": 1,
          "ScenarioId": "ordering",
          "Version": "1.0.0",
          "Seed": 20260729,
          "StartTimeUtc": "2026-07-29T08:00:00Z",
          "DurationMilliseconds": 1000,
          "StopOnAssertionFailure": true,
          "Actions": [
            { "Id": "b", "AtMilliseconds": 100, "Order": 1, "Kind": "state.set", "Target": "value", "Payload": 2 },
            { "Id": "a", "AtMilliseconds": 100, "Order": 0, "Kind": "state.set", "Target": "value", "Payload": 1 }
          ],
          "Assertions": [
            { "Id": "c", "AtMilliseconds": 100, "Order": 0, "Kind": "state.equals", "Target": "value", "Expected": 2 }
          ]
        }
        """;

        var definition = SimulationScenarioDocument.Parse(Encoding.UTF8.GetBytes(json));
        var timeline = SimulationScenarioDocument.BuildTimeline(definition);

        Assert.Equal(["a", "b", "c"], timeline.Select(static item => item.Id).ToArray());
        Assert.Equal(
            [SimulationTimelineItemType.Action, SimulationTimelineItemType.Action, SimulationTimelineItemType.Assertion],
            timeline.Select(static item => item.ItemType).ToArray());
    }

    [Fact]
    public void StrictJsonParser_RejectsUnknownProperties()
    {
        var json = """
        {
          "SchemaVersion": 1,
          "ScenarioId": "unknown-field",
          "Version": "1.0.0",
          "Seed": 20260729,
          "StartTimeUtc": "2026-07-29T08:00:00Z",
          "DurationMilliseconds": 1000,
          "Unexpected": true,
          "Actions": [],
          "Assertions": []
        }
        """;

        Assert.Throws<InvalidOperationException>(() =>
            SimulationScenarioDocument.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task ReplayTwice_WithDifferentSpeed_ProducesIdenticalEvidenceAndState()
    {
        var definition = BasicDefinition();
        var registered = Register(definition);
        var engine = new SimulationScenarioEngine();

        var replay = await engine.ReplayTwiceAsync(registered, definition);

        Assert.True(replay.Equivalent);
        Assert.True(replay.First.Success);
        Assert.True(replay.Second.Success);
        Assert.Equal(replay.FirstEvidenceHash, replay.SecondEvidenceHash);
        Assert.Equal(replay.FirstStateHash, replay.SecondStateHash);
    }

    [Fact]
    public async Task PauseStepCheckpointRestore_MatchesContinuousExecution()
    {
        var definition = BasicDefinition();
        var registered = Register(definition);
        var engine = new SimulationScenarioEngine();

        var continuous = await engine.CreateSession(registered, definition).RunToCompletionAsync();

        var interrupted = engine.CreateSession(registered, definition);
        Assert.True(await interrupted.StepAsync());
        interrupted.Pause();
        var checkpoint = interrupted.CreateCheckpoint();

        var restored = engine.CreateSession(registered, definition, checkpoint);
        Assert.Equal(SimulationSessionStatus.Paused, restored.Status);
        var resumed = await restored.RunToCompletionAsync();

        Assert.True(resumed.Success);
        Assert.Equal(continuous.FinalStateHash, resumed.FinalStateHash);
        Assert.Equal(continuous.Evidence.EvidenceHash, resumed.Evidence.EvidenceHash);
        Assert.Equal(continuous.Assertions, resumed.Assertions);
    }

    [Fact]
    public void Checkpoint_Tampering_IsRejected()
    {
        var definition = BasicDefinition();
        var registered = Register(definition);
        var engine = new SimulationScenarioEngine();
        var session = engine.CreateSession(registered, definition);
        var checkpoint = session.CreateCheckpoint();
        var tampered = checkpoint with { StateJson = "{\"tampered\":true}" };

        Assert.Throws<InvalidOperationException>(() =>
            engine.CreateSession(registered, definition, tampered));
    }

    [Fact]
    public void RandomState_RestoreContinuesTheExactSequence()
    {
        var random = new DeterministicSimulationRandom(20260729);
        _ = Enumerable.Range(0, 20).Select(_ => random.NextUInt64()).ToArray();
        var state = random.CaptureState();
        var expected = Enumerable.Range(0, 20).Select(_ => random.NextUInt64()).ToArray();

        var restored = new DeterministicSimulationRandom(1);
        restored.RestoreState(state);
        var actual = Enumerable.Range(0, 20).Select(_ => restored.NextUInt64()).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FailedAssertion_StopsTheScenarioWhenConfigured()
    {
        var definition = BasicDefinition();
        definition.Assertions[0].Expected = Json("999");
        var registered = Register(definition);

        var result = await new SimulationScenarioEngine()
            .CreateSession(registered, definition)
            .RunToCompletionAsync();

        Assert.False(result.Success);
        Assert.Equal(SimulationSessionStatus.Failed, result.Status);
        Assert.Single(result.Assertions);
        Assert.False(result.Assertions[0].Passed);
        Assert.Contains("Assertion", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioValidation_RejectsDuplicateIdsAndOutOfRangeTime()
    {
        var duplicate = BasicDefinition();
        duplicate.Assertions[0].Id = duplicate.Actions[0].Id;
        Assert.Throws<InvalidOperationException>(() =>
            SimulationScenarioDocument.Validate(duplicate, new SimulationScenarioEngineOptions()));

        var outOfRange = BasicDefinition();
        outOfRange.Actions[0].AtMilliseconds = outOfRange.DurationMilliseconds + 1;
        Assert.Throws<InvalidOperationException>(() =>
            SimulationScenarioDocument.Validate(outOfRange, new SimulationScenarioEngineOptions()));
    }

    [Fact]
    public void ScenarioIdentity_MustMatchTheGovernedManifest()
    {
        var definition = BasicDefinition();
        var registered = Register(definition);
        definition.Version = "2.0.0";

        Assert.Throws<InvalidOperationException>(() =>
            new SimulationScenarioEngine().CreateSession(registered, definition));
    }

    [Fact]
    public async Task TenThousandTimelineEvents_AreNeitherLostNorDuplicated()
    {
        const int eventCount = 10_000;
        var definition = new SimulationScenarioDefinition
        {
            ScenarioId = "ten-thousand-events",
            Version = "1.0.0",
            Seed = 20260729,
            StartTimeUtc = StartTimeUtc,
            DurationMilliseconds = eventCount + 10,
            Actions = Enumerable.Range(0, eventCount)
                .Select(index => new SimulationActionDefinition
                {
                    Id = $"increment-{index:D5}",
                    AtMilliseconds = index,
                    Order = 0,
                    Kind = "state.increment",
                    Target = "counter",
                    Payload = Json("1")
                })
                .ToList(),
            Assertions =
            [
                new SimulationAssertionDefinition
                {
                    Id = "counter-conservation",
                    AtMilliseconds = eventCount,
                    Order = 0,
                    Kind = "state.equals",
                    Target = "counter",
                    Expected = Json(eventCount.ToString())
                }
            ]
        };
        var options = new SimulationScenarioEngineOptions
        {
            MaximumTimelineItems = 20_000,
            MaximumStateEntries = 100,
            MaximumStateValueCharacters = 4_096,
            MaximumCheckpointBytes = 16 * 1024 * 1024,
            MaximumSpeedFactor = 1_000
        };
        var registered = Register(definition);

        var result = await new SimulationScenarioEngine(options: options)
            .CreateSession(registered, definition)
            .RunToCompletionAsync();

        Assert.True(result.Success);
        Assert.Equal(eventCount + 1, result.ExecutedTimelineItems);
        Assert.True(result.Assertions.Single().Passed);
        Assert.Equal(eventCount + 1, result.Evidence.Records.Count);
        Assert.Contains($"\"counter\":{eventCount}", result.FinalStateJson, StringComparison.Ordinal);
    }

    private static SimulationScenarioDefinition BasicDefinition() => new()
    {
        ScenarioId = "deterministic-basic",
        Version = "1.0.0",
        Seed = 20260729,
        StartTimeUtc = StartTimeUtc,
        DurationMilliseconds = 1000,
        Actions =
        [
            new SimulationActionDefinition
            {
                Id = "set-counter",
                AtMilliseconds = 100,
                Order = 0,
                Kind = "state.set",
                Target = "counter",
                Payload = Json("1")
            },
            new SimulationActionDefinition
            {
                Id = "increment-counter",
                AtMilliseconds = 200,
                Order = 0,
                Kind = "state.increment",
                Target = "counter",
                Payload = Json("2")
            },
            new SimulationActionDefinition
            {
                Id = "emit-complete",
                AtMilliseconds = 250,
                Order = 0,
                Kind = "event.emit",
                Target = "scenario.complete",
                Payload = Json("{\"source\":\"test\"}")
            }
        ],
        Assertions =
        [
            new SimulationAssertionDefinition
            {
                Id = "counter-is-three",
                AtMilliseconds = 300,
                Order = 0,
                Kind = "state.equals",
                Target = "counter",
                Expected = Json("3")
            },
            new SimulationAssertionDefinition
            {
                Id = "counter-exists",
                AtMilliseconds = 300,
                Order = 1,
                Kind = "state.exists",
                Target = "counter",
                Expected = Json("null")
            }
        ]
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
            Source = "s1-test",
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

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
