namespace Wcs.Simulator.ScenarioEngine;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Wcs.Simulator.Governance;

public sealed class SimulationScenarioEngineOptions
{
    public int MaximumTimelineItems { get; set; } = 100_000;
    public int MaximumStateEntries { get; set; } = 10_000;
    public int MaximumStateValueCharacters { get; set; } = 4_096;
    public int MaximumCheckpointBytes { get; set; } = 16 * 1024 * 1024;
    public double MaximumSpeedFactor { get; set; } = 1_000;

    public void Validate()
    {
        if (MaximumTimelineItems is < 1 or > 1_000_000)
            throw new InvalidOperationException("MaximumTimelineItems must be between 1 and 1,000,000.");
        if (MaximumStateEntries is < 1 or > 1_000_000)
            throw new InvalidOperationException("MaximumStateEntries must be between 1 and 1,000,000.");
        if (MaximumStateValueCharacters is < 1 or > 1_048_576)
            throw new InvalidOperationException("MaximumStateValueCharacters must be between 1 and 1,048,576.");
        if (MaximumCheckpointBytes is < 1_024 or > 256 * 1024 * 1024)
            throw new InvalidOperationException("MaximumCheckpointBytes must be between 1 KB and 256 MB.");
        if (!double.IsFinite(MaximumSpeedFactor) || MaximumSpeedFactor is < 1 or > 100_000)
            throw new InvalidOperationException("MaximumSpeedFactor must be between 1 and 100,000.");
    }
}

public enum SimulationSessionStatus
{
    Created,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum SimulationTimelineItemType
{
    Action = 0,
    Assertion = 1
}

public sealed class SimulationScenarioDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string ScenarioId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long Seed { get; set; }
    public DateTimeOffset StartTimeUtc { get; set; }
    public long DurationMilliseconds { get; set; }
    public bool StopOnAssertionFailure { get; set; } = true;
    public List<SimulationActionDefinition> Actions { get; set; } = [];
    public List<SimulationAssertionDefinition> Assertions { get; set; } = [];
}

public sealed class SimulationActionDefinition
{
    public string Id { get; set; } = string.Empty;
    public long AtMilliseconds { get; set; }
    public int Order { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public JsonElement Payload { get; set; }
}

public sealed class SimulationAssertionDefinition
{
    public string Id { get; set; } = string.Empty;
    public long AtMilliseconds { get; set; }
    public int Order { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public JsonElement Expected { get; set; }
}

public sealed record SimulationTimelineItem(
    long AtMilliseconds,
    SimulationTimelineItemType ItemType,
    int Order,
    string Id,
    SimulationActionDefinition? Action,
    SimulationAssertionDefinition? Assertion);

public sealed record SimulationAssertionOutcome(
    string AssertionId,
    bool Passed,
    string Kind,
    string Target,
    string Expected,
    string Actual,
    string Message,
    long AtMilliseconds);

public sealed record SimulationCheckpoint(
    string ScenarioId,
    string ScenarioVersion,
    string ScenarioManifestHash,
    long Seed,
    long CurrentOffsetMilliseconds,
    int NextTimelineIndex,
    ulong RandomState,
    string StateJson,
    IReadOnlyList<SimulationEvidenceRecord> EvidenceRecords,
    IReadOnlyList<SimulationAssertionOutcome> AssertionOutcomes,
    string CheckpointHash)
{
    public static SimulationCheckpoint Create(
        RegisteredSimulationScenario scenario,
        long currentOffsetMilliseconds,
        int nextTimelineIndex,
        ulong randomState,
        SimulationStateStore state,
        IReadOnlyList<SimulationEvidenceRecord> evidenceRecords,
        IReadOnlyList<SimulationAssertionOutcome> assertionOutcomes,
        SimulationScenarioEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidenceRecords);
        ArgumentNullException.ThrowIfNull(assertionOutcomes);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (currentOffsetMilliseconds < 0)
            throw new InvalidOperationException("Checkpoint offset cannot be negative.");
        if (nextTimelineIndex < 0)
            throw new InvalidOperationException("Checkpoint timeline index cannot be negative.");

        var stateJson = state.ToCanonicalJson();
        var canonical = JsonSerializer.Serialize(new
        {
            scenario.ScenarioId,
            ScenarioVersion = scenario.Version,
            scenario.ManifestHash,
            scenario.Seed,
            CurrentOffsetMilliseconds = currentOffsetMilliseconds,
            NextTimelineIndex = nextTimelineIndex,
            RandomState = randomState,
            StateJson = stateJson,
            EvidenceRecords = evidenceRecords.OrderBy(static item => item.Sequence),
            AssertionOutcomes = assertionOutcomes.OrderBy(static item => item.AtMilliseconds)
                .ThenBy(static item => item.AssertionId, StringComparer.Ordinal)
        });

        if (Encoding.UTF8.GetByteCount(canonical) > options.MaximumCheckpointBytes)
            throw new InvalidOperationException("Checkpoint exceeds MaximumCheckpointBytes.");

        return new SimulationCheckpoint(
            scenario.ScenarioId,
            scenario.Version,
            scenario.ManifestHash,
            scenario.Seed,
            currentOffsetMilliseconds,
            nextTimelineIndex,
            randomState,
            stateJson,
            evidenceRecords.OrderBy(static item => item.Sequence).ToArray(),
            assertionOutcomes.OrderBy(static item => item.AtMilliseconds)
                .ThenBy(static item => item.AssertionId, StringComparer.Ordinal)
                .ToArray(),
            SimulationScenarioValidator.ComputeSha256(Encoding.UTF8.GetBytes(canonical)));
    }

    public void Validate(RegisteredSimulationScenario scenario, SimulationScenarioEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(options);
        var recreated = Create(
            scenario,
            CurrentOffsetMilliseconds,
            NextTimelineIndex,
            RandomState,
            SimulationStateStore.FromCanonicalJson(StateJson, options),
            EvidenceRecords,
            AssertionOutcomes,
            options);

        if (!string.Equals(ScenarioId, scenario.ScenarioId, StringComparison.Ordinal) ||
            !string.Equals(ScenarioVersion, scenario.Version, StringComparison.Ordinal) ||
            !string.Equals(ScenarioManifestHash, scenario.ManifestHash, StringComparison.OrdinalIgnoreCase) ||
            Seed != scenario.Seed ||
            !string.Equals(CheckpointHash, recreated.CheckpointHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Checkpoint identity or SHA-256 validation failed.");
    }
}

public static partial class SimulationScenarioDocument
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex KindRegex();

    public static SimulationScenarioDefinition Parse(
        ReadOnlySpan<byte> content,
        SimulationScenarioEngineOptions? options = null)
    {
        options ??= new SimulationScenarioEngineOptions();
        options.Validate();

        try
        {
            var definition = JsonSerializer.Deserialize<SimulationScenarioDefinition>(content, SerializerOptions)
                ?? throw new InvalidOperationException("Scenario document is empty.");
            Validate(definition, options);
            return definition;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Scenario document is not valid strict JSON.", exception);
        }
    }

    public static void Validate(
        SimulationScenarioDefinition definition,
        SimulationScenarioEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (definition.SchemaVersion != 1)
            throw new InvalidOperationException("Only scenario DSL schema version 1 is supported.");
        ValidateIdentifier(definition.ScenarioId, nameof(definition.ScenarioId));
        ValidateIdentifier(definition.Version, nameof(definition.Version));
        if (definition.Seed == 0)
            throw new InvalidOperationException("Scenario Seed must be non-zero.");
        if (definition.StartTimeUtc == default)
            throw new InvalidOperationException("Scenario StartTimeUtc is required.");
        if (definition.DurationMilliseconds is < 1 or > 31_536_000_000)
            throw new InvalidOperationException("Scenario duration must be between 1 millisecond and 365 days.");

        definition.Actions ??= [];
        definition.Assertions ??= [];
        var count = checked(definition.Actions.Count + definition.Assertions.Count);
        if (count > options.MaximumTimelineItems)
            throw new InvalidOperationException("Scenario timeline exceeds MaximumTimelineItems.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in definition.Actions)
        {
            ValidateTimelineIdentity(action.Id, action.Kind, action.Target, action.AtMilliseconds, action.Order, definition.DurationMilliseconds);
            if (!ids.Add(action.Id))
                throw new InvalidOperationException($"Duplicate timeline item id '{action.Id}'.");
        }

        foreach (var assertion in definition.Assertions)
        {
            ValidateTimelineIdentity(assertion.Id, assertion.Kind, assertion.Target, assertion.AtMilliseconds, assertion.Order, definition.DurationMilliseconds);
            if (!ids.Add(assertion.Id))
                throw new InvalidOperationException($"Duplicate timeline item id '{assertion.Id}'.");
        }
    }

    public static IReadOnlyList<SimulationTimelineItem> BuildTimeline(SimulationScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Actions
            .Select(static item => new SimulationTimelineItem(
                item.AtMilliseconds,
                SimulationTimelineItemType.Action,
                item.Order,
                item.Id,
                item,
                null))
            .Concat(definition.Assertions.Select(static item => new SimulationTimelineItem(
                item.AtMilliseconds,
                SimulationTimelineItemType.Assertion,
                item.Order,
                item.Id,
                null,
                item)))
            .OrderBy(static item => item.AtMilliseconds)
            .ThenBy(static item => item.ItemType)
            .ThenBy(static item => item.Order)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value))
            throw new InvalidOperationException($"Scenario {name} contains unsupported characters.");
    }

    private static void ValidateTimelineIdentity(
        string? id,
        string? kind,
        string? target,
        long atMilliseconds,
        int order,
        long durationMilliseconds)
    {
        ValidateIdentifier(id, "timeline item id");
        if (string.IsNullOrWhiteSpace(kind) || !KindRegex().IsMatch(kind))
            throw new InvalidOperationException($"Timeline item '{id}' has an invalid kind.");
        if (string.IsNullOrWhiteSpace(target) || target.Length > 256)
            throw new InvalidOperationException($"Timeline item '{id}' target is required and cannot exceed 256 characters.");
        if (atMilliseconds < 0 || atMilliseconds > durationMilliseconds)
            throw new InvalidOperationException($"Timeline item '{id}' is outside the scenario duration.");
        if (order < 0)
            throw new InvalidOperationException($"Timeline item '{id}' order cannot be negative.");
    }
}

public sealed class SimulationStateStore
{
    private readonly SortedDictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private readonly SimulationScenarioEngineOptions _options;

    public SimulationStateStore(SimulationScenarioEngineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public int Count => _values.Count;

    public void Set(string key, JsonElement value)
    {
        ValidateKey(key);
        var raw = value.GetRawText();
        if (raw.Length > _options.MaximumStateValueCharacters)
            throw new InvalidOperationException("Simulation state value exceeds MaximumStateValueCharacters.");
        if (!_values.ContainsKey(key) && _values.Count >= _options.MaximumStateEntries)
            throw new InvalidOperationException("Simulation state store has reached MaximumStateEntries.");
        _values[key] = value.Clone();
    }

    public long Increment(string key, long delta)
    {
        ValidateKey(key);
        long current = 0;
        if (_values.TryGetValue(key, out var existing))
        {
            if (existing.ValueKind != JsonValueKind.Number || !existing.TryGetInt64(out current))
                throw new InvalidOperationException($"Simulation state '{key}' is not an Int64 counter.");
        }

        var updated = checked(current + delta);
        using var document = JsonDocument.Parse(updated.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set(key, document.RootElement);
        return updated;
    }

    public bool Contains(string key) => _values.ContainsKey(key);

    public bool TryGet(string key, out JsonElement value)
    {
        if (_values.TryGetValue(key, out var stored))
        {
            value = stored.Clone();
            return true;
        }

        value = default;
        return false;
    }

    public string ToCanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var pair in _values)
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ComputeHash() =>
        SimulationScenarioValidator.ComputeSha256(Encoding.UTF8.GetBytes(ToCanonicalJson()));

    public static SimulationStateStore FromCanonicalJson(
        string stateJson,
        SimulationScenarioEngineOptions options)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
            throw new InvalidOperationException("Checkpoint state JSON is required.");

        using var document = JsonDocument.Parse(stateJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Checkpoint state JSON must be an object.");

        var store = new SimulationStateStore(options);
        foreach (var property in document.RootElement.EnumerateObject())
            store.Set(property.Name, property.Value);
        return store;
    }

    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            throw new InvalidOperationException("Simulation state key is required and cannot exceed 256 characters.");
    }
}
