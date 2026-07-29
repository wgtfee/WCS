namespace Wcs.Simulator.ScenarioEngine;

using System.Text.Json;
using System.Text.Json.Nodes;
using Wcs.Simulator.Governance;

public sealed record SimulationActionContext(
    SimulationActionDefinition Definition,
    SimulationStateStore State,
    DeterministicSimulationClock Clock,
    DeterministicSimulationRandom Random);

public sealed record SimulationAssertionContext(
    SimulationAssertionDefinition Definition,
    SimulationStateStore State,
    DeterministicSimulationClock Clock);

public sealed record SimulationActionOutcome(string Value);

public interface ISimulationActionHandler
{
    string Kind { get; }
    ValueTask<SimulationActionOutcome> ExecuteAsync(
        SimulationActionContext context,
        CancellationToken cancellationToken);
}

public interface ISimulationAssertionHandler
{
    string Kind { get; }
    ValueTask<SimulationAssertionOutcome> EvaluateAsync(
        SimulationAssertionContext context,
        CancellationToken cancellationToken);
}

public sealed record SimulationRunResult(
    string ScenarioId,
    string ScenarioVersion,
    string ScenarioManifestHash,
    SimulationSessionStatus Status,
    bool Success,
    string? FailureMessage,
    long CurrentOffsetMilliseconds,
    int ExecutedTimelineItems,
    string FinalStateJson,
    string FinalStateHash,
    IReadOnlyList<SimulationAssertionOutcome> Assertions,
    SimulationEvidenceEnvelope Evidence);

public sealed record SimulationReplayComparison(
    bool Equivalent,
    string FirstEvidenceHash,
    string SecondEvidenceHash,
    string FirstStateHash,
    string SecondStateHash,
    SimulationRunResult First,
    SimulationRunResult Second);

public sealed class SimulationScenarioEngine
{
    private readonly IReadOnlyDictionary<string, ISimulationActionHandler> _actionHandlers;
    private readonly IReadOnlyDictionary<string, ISimulationAssertionHandler> _assertionHandlers;
    private readonly SimulationScenarioEngineOptions _options;

    public SimulationScenarioEngine(
        IEnumerable<ISimulationActionHandler>? actionHandlers = null,
        IEnumerable<ISimulationAssertionHandler>? assertionHandlers = null,
        SimulationScenarioEngineOptions? options = null)
    {
        _options = options ?? new SimulationScenarioEngineOptions();
        _options.Validate();

        var actions = new ISimulationActionHandler[]
        {
            new StateSetActionHandler(),
            new StateIncrementActionHandler(),
            new EventEmitActionHandler()
        }.Concat(actionHandlers ?? []).ToArray();
        var assertions = new ISimulationAssertionHandler[]
        {
            new StateEqualsAssertionHandler(),
            new StateExistsAssertionHandler(expectedExists: true),
            new StateExistsAssertionHandler(expectedExists: false)
        }.Concat(assertionHandlers ?? []).ToArray();

        _actionHandlers = BuildUnique(actions, static item => item.Kind, "action");
        _assertionHandlers = BuildUnique(assertions, static item => item.Kind, "assertion");
    }

    public SimulationExecutionSession CreateSession(
        RegisteredSimulationScenario registeredScenario,
        SimulationScenarioDefinition definition,
        SimulationCheckpoint? checkpoint = null,
        double speedFactor = 1)
    {
        ArgumentNullException.ThrowIfNull(registeredScenario);
        ArgumentNullException.ThrowIfNull(definition);
        SimulationScenarioDocument.Validate(definition, _options);
        ValidateIdentity(registeredScenario, definition);

        return new SimulationExecutionSession(
            registeredScenario,
            definition,
            _actionHandlers,
            _assertionHandlers,
            _options,
            checkpoint,
            speedFactor);
    }

    public async Task<SimulationReplayComparison> ReplayTwiceAsync(
        RegisteredSimulationScenario registeredScenario,
        SimulationScenarioDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var firstSession = CreateSession(registeredScenario, definition, speedFactor: 1);
        var first = await firstSession.RunToCompletionAsync(cancellationToken);
        var secondSession = CreateSession(registeredScenario, definition, speedFactor: 100);
        var second = await secondSession.RunToCompletionAsync(cancellationToken);

        var equivalent =
            string.Equals(first.Evidence.EvidenceHash, second.Evidence.EvidenceHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(first.FinalStateHash, second.FinalStateHash, StringComparison.OrdinalIgnoreCase) &&
            first.Assertions.SequenceEqual(second.Assertions);

        return new SimulationReplayComparison(
            equivalent,
            first.Evidence.EvidenceHash,
            second.Evidence.EvidenceHash,
            first.FinalStateHash,
            second.FinalStateHash,
            first,
            second);
    }

    private static IReadOnlyDictionary<string, T> BuildUnique<T>(
        IEnumerable<T> handlers,
        Func<T, string> kindSelector,
        string category)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            var kind = kindSelector(handler);
            if (string.IsNullOrWhiteSpace(kind))
                throw new InvalidOperationException($"Simulation {category} handler kind is required.");
            if (!result.TryAdd(kind, handler))
                throw new InvalidOperationException($"Duplicate simulation {category} handler kind '{kind}'.");
        }
        return result;
    }

    private static void ValidateIdentity(
        RegisteredSimulationScenario registeredScenario,
        SimulationScenarioDefinition definition)
    {
        if (!string.Equals(registeredScenario.ScenarioId, definition.ScenarioId, StringComparison.Ordinal) ||
            !string.Equals(registeredScenario.Version, definition.Version, StringComparison.Ordinal) ||
            registeredScenario.Seed != definition.Seed)
            throw new InvalidOperationException("Scenario DSL identity does not match its governed manifest.");
    }
}

public sealed class SimulationExecutionSession
{
    private readonly RegisteredSimulationScenario _registeredScenario;
    private readonly SimulationScenarioDefinition _definition;
    private readonly IReadOnlyDictionary<string, ISimulationActionHandler> _actionHandlers;
    private readonly IReadOnlyDictionary<string, ISimulationAssertionHandler> _assertionHandlers;
    private readonly SimulationScenarioEngineOptions _options;
    private readonly IReadOnlyList<SimulationTimelineItem> _timeline;
    private readonly List<SimulationEvidenceRecord> _evidence;
    private readonly List<SimulationAssertionOutcome> _assertions;
    private long _nextEvidenceSequence;
    private int _nextTimelineIndex;

    internal SimulationExecutionSession(
        RegisteredSimulationScenario registeredScenario,
        SimulationScenarioDefinition definition,
        IReadOnlyDictionary<string, ISimulationActionHandler> actionHandlers,
        IReadOnlyDictionary<string, ISimulationAssertionHandler> assertionHandlers,
        SimulationScenarioEngineOptions options,
        SimulationCheckpoint? checkpoint,
        double speedFactor)
    {
        _registeredScenario = registeredScenario;
        _definition = definition;
        _actionHandlers = actionHandlers;
        _assertionHandlers = assertionHandlers;
        _options = options;
        _timeline = SimulationScenarioDocument.BuildTimeline(definition);
        Clock = new DeterministicSimulationClock(definition.StartTimeUtc, definition.DurationMilliseconds, speedFactor);
        Clock.SetSpeed(speedFactor, options.MaximumSpeedFactor);
        Random = new DeterministicSimulationRandom(definition.Seed);
        State = new SimulationStateStore(options);
        _evidence = [];
        _assertions = [];
        Status = SimulationSessionStatus.Created;

        if (checkpoint is not null)
            Restore(checkpoint);
    }

    public SimulationSessionStatus Status { get; private set; }
    public string? FailureMessage { get; private set; }
    public DeterministicSimulationClock Clock { get; }
    public DeterministicSimulationRandom Random { get; }
    public SimulationStateStore State { get; private set; }
    public int NextTimelineIndex => _nextTimelineIndex;
    public int TimelineCount => _timeline.Count;
    public IReadOnlyList<SimulationAssertionOutcome> AssertionOutcomes => _assertions.ToArray();

    public void SetSpeed(double speedFactor) =>
        Clock.SetSpeed(speedFactor, _options.MaximumSpeedFactor);

    public void Pause()
    {
        if (Status is SimulationSessionStatus.Completed or SimulationSessionStatus.Failed or SimulationSessionStatus.Cancelled)
            throw new InvalidOperationException("A terminal simulation session cannot be paused.");
        Status = SimulationSessionStatus.Paused;
    }

    public void Resume()
    {
        if (Status is SimulationSessionStatus.Completed or SimulationSessionStatus.Failed or SimulationSessionStatus.Cancelled)
            throw new InvalidOperationException("A terminal simulation session cannot be resumed.");
        Status = SimulationSessionStatus.Running;
    }

    public async ValueTask<bool> StepAsync(CancellationToken cancellationToken = default)
    {
        if (Status is SimulationSessionStatus.Completed or SimulationSessionStatus.Failed or SimulationSessionStatus.Cancelled)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var preservePaused = Status == SimulationSessionStatus.Paused;
        if (Status == SimulationSessionStatus.Created)
            Status = SimulationSessionStatus.Running;

        if (_nextTimelineIndex >= _timeline.Count)
        {
            Clock.AdvanceTo(_definition.DurationMilliseconds);
            Status = SimulationSessionStatus.Completed;
            return false;
        }

        var item = _timeline[_nextTimelineIndex];
        Clock.AdvanceTo(item.AtMilliseconds);

        try
        {
            if (item.ItemType == SimulationTimelineItemType.Action)
                await ExecuteActionAsync(item.Action!, cancellationToken);
            else
                await ExecuteAssertionAsync(item.Assertion!, cancellationToken);

            _nextTimelineIndex++;
            if (_nextTimelineIndex >= _timeline.Count)
            {
                Clock.AdvanceTo(_definition.DurationMilliseconds);
                if (Status != SimulationSessionStatus.Failed)
                    Status = SimulationSessionStatus.Completed;
            }
            else if (preservePaused && Status != SimulationSessionStatus.Failed)
            {
                Status = SimulationSessionStatus.Paused;
            }

            return Status != SimulationSessionStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            Status = SimulationSessionStatus.Cancelled;
            FailureMessage = "Simulation execution was cancelled.";
            throw;
        }
        catch (Exception exception)
        {
            Status = SimulationSessionStatus.Failed;
            FailureMessage = exception.Message;
            AddEvidence("engine", "failed", BoundValue($"{exception.GetType().Name}:{exception.Message}"));
            return false;
        }
    }

    public async Task RunUntilAsync(long targetOffsetMilliseconds, CancellationToken cancellationToken = default)
    {
        if (Status == SimulationSessionStatus.Paused)
            throw new InvalidOperationException("Resume the simulation session before running to a target time.");
        if (targetOffsetMilliseconds < Clock.CurrentOffsetMilliseconds ||
            targetOffsetMilliseconds > _definition.DurationMilliseconds)
            throw new InvalidOperationException("Target time is outside the forward simulation window.");
        if (Status == SimulationSessionStatus.Created)
            Status = SimulationSessionStatus.Running;

        while (_nextTimelineIndex < _timeline.Count &&
               _timeline[_nextTimelineIndex].AtMilliseconds <= targetOffsetMilliseconds &&
               Status == SimulationSessionStatus.Running)
        {
            var continued = await StepAsync(cancellationToken);
            if (!continued)
                break;
        }

        if (Status == SimulationSessionStatus.Running)
        {
            Clock.AdvanceTo(targetOffsetMilliseconds);
            if (targetOffsetMilliseconds == _definition.DurationMilliseconds && _nextTimelineIndex >= _timeline.Count)
                Status = SimulationSessionStatus.Completed;
        }
    }

    public async Task<SimulationRunResult> RunToCompletionAsync(CancellationToken cancellationToken = default)
    {
        if (Status == SimulationSessionStatus.Paused || Status == SimulationSessionStatus.Created)
            Resume();

        while (Status == SimulationSessionStatus.Running)
        {
            if (_nextTimelineIndex >= _timeline.Count)
            {
                Clock.AdvanceTo(_definition.DurationMilliseconds);
                Status = SimulationSessionStatus.Completed;
                break;
            }

            var continued = await StepAsync(cancellationToken);
            if (!continued && Status != SimulationSessionStatus.Completed)
                break;
        }

        return BuildResult();
    }

    public SimulationCheckpoint CreateCheckpoint() =>
        SimulationCheckpoint.Create(
            _registeredScenario,
            Clock.CurrentOffsetMilliseconds,
            _nextTimelineIndex,
            Random.CaptureState(),
            State,
            _evidence,
            _assertions,
            _options);

    public SimulationRunResult BuildResult()
    {
        if (Status is SimulationSessionStatus.Created or SimulationSessionStatus.Running or SimulationSessionStatus.Paused)
            throw new InvalidOperationException("Simulation result is available only after terminal completion.");

        var finalStateJson = State.ToCanonicalJson();
        var evidence = SimulationEvidenceEnvelope.Create(
            _registeredScenario,
            _definition.StartTimeUtc,
            Clock.CurrentTimeUtc,
            _evidence,
            new SimulationGovernanceOptions
            {
                Enabled = true,
                MaximumScenarioBytes = 1,
                MaximumRegisteredScenarioVersions = 1,
                MaximumEvidenceRecords = Math.Max(1, _options.MaximumTimelineItems * 2),
                MaximumEvidenceValueCharacters = _options.MaximumStateValueCharacters,
                AllowedEnvironments = ["Simulation"]
            });

        return new SimulationRunResult(
            _registeredScenario.ScenarioId,
            _registeredScenario.Version,
            _registeredScenario.ManifestHash,
            Status,
            Status == SimulationSessionStatus.Completed && _assertions.All(static item => item.Passed),
            FailureMessage,
            Clock.CurrentOffsetMilliseconds,
            _nextTimelineIndex,
            finalStateJson,
            State.ComputeHash(),
            _assertions.ToArray(),
            evidence);
    }

    private async ValueTask ExecuteActionAsync(
        SimulationActionDefinition action,
        CancellationToken cancellationToken)
    {
        if (!_actionHandlers.TryGetValue(action.Kind, out var handler))
            throw new InvalidOperationException($"No simulation action handler is registered for '{action.Kind}'.");

        var outcome = await handler.ExecuteAsync(
            new SimulationActionContext(action, State, Clock, Random),
            cancellationToken);
        AddEvidence("action", action.Id, BoundValue(outcome.Value));
    }

    private async ValueTask ExecuteAssertionAsync(
        SimulationAssertionDefinition assertion,
        CancellationToken cancellationToken)
    {
        if (!_assertionHandlers.TryGetValue(assertion.Kind, out var handler))
            throw new InvalidOperationException($"No simulation assertion handler is registered for '{assertion.Kind}'.");

        var outcome = await handler.EvaluateAsync(
            new SimulationAssertionContext(assertion, State, Clock),
            cancellationToken);
        _assertions.Add(outcome);
        AddEvidence("assertion", assertion.Id, BoundValue(JsonSerializer.Serialize(new
        {
            outcome.Passed,
            outcome.Kind,
            outcome.Target,
            outcome.Expected,
            outcome.Actual,
            outcome.Message
        })));

        if (!outcome.Passed && _definition.StopOnAssertionFailure)
        {
            Status = SimulationSessionStatus.Failed;
            FailureMessage = $"Assertion '{assertion.Id}' failed: {outcome.Message}";
        }
    }

    private void AddEvidence(string category, string name, string value)
    {
        _evidence.Add(new SimulationEvidenceRecord(
            _nextEvidenceSequence++,
            category,
            name,
            value,
            Clock.CurrentTimeUtc));
    }

    private string BoundValue(string value) =>
        value.Length <= _options.MaximumStateValueCharacters
            ? value
            : value[.._options.MaximumStateValueCharacters];

    private void Restore(SimulationCheckpoint checkpoint)
    {
        checkpoint.Validate(_registeredScenario, _options);
        if (checkpoint.NextTimelineIndex > _timeline.Count)
            throw new InvalidOperationException("Checkpoint timeline index exceeds the current scenario timeline.");
        if (checkpoint.CurrentOffsetMilliseconds > _definition.DurationMilliseconds)
            throw new InvalidOperationException("Checkpoint offset exceeds the current scenario duration.");
        if (checkpoint.NextTimelineIndex < _timeline.Count &&
            _timeline[checkpoint.NextTimelineIndex].AtMilliseconds < checkpoint.CurrentOffsetMilliseconds)
            throw new InvalidOperationException("Checkpoint timeline index is behind its virtual clock.");

        Clock.Restore(checkpoint.CurrentOffsetMilliseconds);
        Random.RestoreState(checkpoint.RandomState);
        State = SimulationStateStore.FromCanonicalJson(checkpoint.StateJson, _options);
        _evidence.AddRange(checkpoint.EvidenceRecords.OrderBy(static item => item.Sequence));
        _assertions.AddRange(checkpoint.AssertionOutcomes);
        _nextTimelineIndex = checkpoint.NextTimelineIndex;
        _nextEvidenceSequence = _evidence.Count == 0 ? 0 : checked(_evidence.Max(static item => item.Sequence) + 1);
        Status = SimulationSessionStatus.Paused;
    }
}

internal sealed class StateSetActionHandler : ISimulationActionHandler
{
    public string Kind => "state.set";

    public ValueTask<SimulationActionOutcome> ExecuteAsync(
        SimulationActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Definition.Payload.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("state.set requires a payload.");
        context.State.Set(context.Definition.Target, context.Definition.Payload);
        return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new
        {
            kind = Kind,
            context.Definition.Target,
            value = context.Definition.Payload
        })));
    }
}

internal sealed class StateIncrementActionHandler : ISimulationActionHandler
{
    public string Kind => "state.increment";

    public ValueTask<SimulationActionOutcome> ExecuteAsync(
        SimulationActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Definition.Payload.ValueKind != JsonValueKind.Number ||
            !context.Definition.Payload.TryGetInt64(out var delta))
            throw new InvalidOperationException("state.increment payload must be an Int64 number.");
        var updated = context.State.Increment(context.Definition.Target, delta);
        return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new
        {
            kind = Kind,
            context.Definition.Target,
            delta,
            value = updated
        })));
    }
}

internal sealed class EventEmitActionHandler : ISimulationActionHandler
{
    public string Kind => "event.emit";

    public ValueTask<SimulationActionOutcome> ExecuteAsync(
        SimulationActionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = context.Definition.Payload.ValueKind == JsonValueKind.Undefined
            ? "null"
            : context.Definition.Payload.GetRawText();
        return ValueTask.FromResult(new SimulationActionOutcome(JsonSerializer.Serialize(new
        {
            kind = Kind,
            context.Definition.Target,
            payload = JsonNode.Parse(payload)
        })));
    }
}

internal sealed class StateEqualsAssertionHandler : ISimulationAssertionHandler
{
    public string Kind => "state.equals";

    public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
        SimulationAssertionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = context.State.TryGet(context.Definition.Target, out var actualElement);
        var expectedText = context.Definition.Expected.ValueKind == JsonValueKind.Undefined
            ? "undefined"
            : context.Definition.Expected.GetRawText();
        var actualText = exists ? actualElement.GetRawText() : "missing";
        var passed = exists && JsonEquivalent(actualElement, context.Definition.Expected);
        return ValueTask.FromResult(new SimulationAssertionOutcome(
            context.Definition.Id,
            passed,
            Kind,
            context.Definition.Target,
            expectedText,
            actualText,
            passed ? "State value matched." : "State value did not match.",
            context.Definition.AtMilliseconds));
    }

    private static bool JsonEquivalent(JsonElement actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Undefined)
            return false;
        return JsonNode.DeepEquals(
            JsonNode.Parse(actual.GetRawText()),
            JsonNode.Parse(expected.GetRawText()));
    }
}

internal sealed class StateExistsAssertionHandler : ISimulationAssertionHandler
{
    private readonly bool _expectedExists;

    public StateExistsAssertionHandler(bool expectedExists) =>
        _expectedExists = expectedExists;

    public string Kind => _expectedExists ? "state.exists" : "state.notExists";

    public ValueTask<SimulationAssertionOutcome> EvaluateAsync(
        SimulationAssertionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actualExists = context.State.Contains(context.Definition.Target);
        var passed = actualExists == _expectedExists;
        return ValueTask.FromResult(new SimulationAssertionOutcome(
            context.Definition.Id,
            passed,
            Kind,
            context.Definition.Target,
            _expectedExists.ToString(),
            actualExists.ToString(),
            passed ? "State existence matched." : "State existence did not match.",
            context.Definition.AtMilliseconds));
    }
}
