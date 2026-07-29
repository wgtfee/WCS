namespace Wcs.Simulator.Governance;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Unified simulation governance options. Simulation remains opt-in and is never allowed in Production.
/// </summary>
public sealed class SimulationGovernanceOptions
{
    public const string SectionName = "SimulationGovernance";

    public bool Enabled { get; set; }
    public string ScenarioDirectory { get; set; } = "data/simulation-scenarios";
    public int MaximumScenarioBytes { get; set; } = 1_048_576;
    public int MaximumEvidenceRecords { get; set; } = 10_000;
    public string[] AllowedEnvironments { get; set; } = ["Simulation", "SimulationLoadTest"];

    public void Validate()
    {
        if (MaximumScenarioBytes is < 1 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("SimulationGovernance.MaximumScenarioBytes must be between 1 byte and 16 MB.");
        if (MaximumEvidenceRecords is < 1 or > 1_000_000)
            throw new InvalidOperationException("SimulationGovernance.MaximumEvidenceRecords must be between 1 and 1,000,000.");
        if (string.IsNullOrWhiteSpace(ScenarioDirectory))
            throw new InvalidOperationException("SimulationGovernance.ScenarioDirectory is required.");
        if (AllowedEnvironments.Length == 0)
            throw new InvalidOperationException("SimulationGovernance.AllowedEnvironments cannot be empty.");
        if (AllowedEnvironments.Any(static environment =>
                string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Production can never be an allowed simulation environment.");
        if (AllowedEnvironments.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("SimulationGovernance.AllowedEnvironments cannot contain empty values.");
        if (AllowedEnvironments.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedEnvironments.Length)
            throw new InvalidOperationException("SimulationGovernance.AllowedEnvironments must be unique.");
    }
}

public sealed class SimulationScenarioManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ScenarioId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public long Seed { get; set; }
    public string ScenarioFile { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset ApprovedAtUtc { get; set; }
}

public sealed record SimulationScenarioPackage(
    SimulationScenarioManifest Manifest,
    ReadOnlyMemory<byte> Content);

public sealed record RegisteredSimulationScenario(
    string ScenarioId,
    string Version,
    long Seed,
    string ContentSha256,
    string ManifestHash,
    DateTimeOffset RegisteredAtUtc);

public sealed record SimulationAccessDecision(bool Allowed, string Code, string Message);

public static class SimulationBoundaryGuard
{
    public static SimulationAccessDecision Evaluate(
        string environmentName,
        SimulationGovernanceOptions options,
        bool simulatorEnabled)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
            return new(false, "production-denied", "Simulation is never available in Production.");
        if (!simulatorEnabled)
            return new(false, "simulator-disabled", "Simulator.Enabled is false.");
        if (!options.Enabled)
            return new(false, "governance-disabled", "SimulationGovernance.Enabled is false.");
        if (!options.AllowedEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase))
            return new(false, "environment-denied", $"Environment '{environmentName}' is not approved for simulation.");

        return new(true, "allowed", "Simulation governance access is allowed.");
    }
}

public static partial class SimulationScenarioValidator
{
    private const int Sha256HexLength = 64;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    public static RegisteredSimulationScenario Validate(
        SimulationScenarioPackage package,
        SimulationGovernanceOptions options,
        DateTimeOffset? registeredAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(package.Manifest);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var manifest = package.Manifest;
        if (manifest.SchemaVersion != 1)
            throw new InvalidOperationException("Only simulation manifest schema version 1 is supported.");
        ValidateIdentifier(manifest.ScenarioId, nameof(manifest.ScenarioId));
        ValidateIdentifier(manifest.Version, nameof(manifest.Version));
        ValidateRelativePath(manifest.ScenarioFile);
        if (manifest.Seed == 0)
            throw new InvalidOperationException("Simulation scenario Seed must be non-zero and explicitly versioned.");
        if (manifest.CreatedAtUtc == default)
            throw new InvalidOperationException("Simulation scenario CreatedAtUtc is required.");
        if (string.IsNullOrWhiteSpace(manifest.Source))
            throw new InvalidOperationException("Simulation scenario Source is required.");
        if (string.IsNullOrWhiteSpace(manifest.ApprovedBy))
            throw new InvalidOperationException("Simulation scenario ApprovedBy is required.");
        if (manifest.ApprovedAtUtc == default)
            throw new InvalidOperationException("Simulation scenario ApprovedAtUtc is required.");
        if (manifest.ApprovedAtUtc < manifest.CreatedAtUtc)
            throw new InvalidOperationException("Simulation scenario approval cannot predate creation.");
        if (package.Content.IsEmpty)
            throw new InvalidOperationException("Simulation scenario content cannot be empty.");
        if (package.Content.Length > options.MaximumScenarioBytes)
            throw new InvalidOperationException("Simulation scenario exceeds MaximumScenarioBytes.");

        var actualContentSha = ComputeSha256(package.Content.Span);
        if (!IsSha256(manifest.ContentSha256) ||
            !string.Equals(actualContentSha, manifest.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Simulation scenario content SHA-256 does not match the manifest.");

        var manifestHash = ComputeManifestHash(manifest, actualContentSha);
        return new RegisteredSimulationScenario(
            manifest.ScenarioId,
            manifest.Version,
            manifest.Seed,
            actualContentSha,
            manifestHash,
            registeredAtUtc ?? DateTimeOffset.UtcNow);
    }

    public static string ComputeManifestHash(SimulationScenarioManifest manifest, string normalizedContentSha256)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = string.Join('\n',
            manifest.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            manifest.ScenarioId.Trim(),
            manifest.Version.Trim(),
            manifest.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NormalizeRelativePath(manifest.ScenarioFile),
            normalizedContentSha256.ToLowerInvariant(),
            manifest.CreatedAtUtc.ToUniversalTime().ToString("O"),
            manifest.Source.Trim(),
            manifest.ApprovedBy.Trim(),
            manifest.ApprovedAtUtc.ToUniversalTime().ToString("O"));
        return ComputeSha256(Encoding.UTF8.GetBytes(canonical));
    }

    public static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value))
            throw new InvalidOperationException($"Simulation scenario {name} contains unsupported characters.");
    }

    private static bool IsSha256(string value) =>
        value.Length == Sha256HexLength && value.All(Uri.IsHexDigit);

    private static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Simulation scenario file is required.");
        if (Path.IsPathRooted(value))
            throw new InvalidOperationException("Simulation scenario file must be relative.");

        var segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new InvalidOperationException("Simulation scenario file cannot contain path traversal.");
    }

    private static string NormalizeRelativePath(string value) =>
        string.Join('/', value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Thread-safe immutable version registry. The same ScenarioId+Version may be registered repeatedly only when hashes match.
/// </summary>
public sealed class SimulationScenarioRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredSimulationScenario> _versions =
        new(StringComparer.OrdinalIgnoreCase);

    public RegisteredSimulationScenario Register(
        SimulationScenarioPackage package,
        SimulationGovernanceOptions options,
        DateTimeOffset? registeredAtUtc = null)
    {
        var candidate = SimulationScenarioValidator.Validate(package, options, registeredAtUtc);
        var key = $"{candidate.ScenarioId}|{candidate.Version}";
        var registered = _versions.GetOrAdd(key, candidate);

        if (!string.Equals(registered.ManifestHash, candidate.ManifestHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(registered.ContentSha256, candidate.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Simulation scenario {candidate.ScenarioId}@{candidate.Version} is immutable and already has different evidence hashes.");

        return registered;
    }

    public IReadOnlyCollection<RegisteredSimulationScenario> List() =>
        _versions.Values
            .OrderBy(static item => item.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Runtime-independent deterministic pseudo-random generator used by all governed scenarios.
/// </summary>
public sealed class DeterministicSimulationRandom
{
    private ulong _state;

    public DeterministicSimulationRandom(long seed)
    {
        if (seed == 0)
            throw new ArgumentOutOfRangeException(nameof(seed), "Simulation seed must be non-zero.");
        _state = unchecked((ulong)seed);
    }

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var value = _state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public int Next(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        return (int)(NextUInt64() % (uint)exclusiveMaximum);
    }

    public double NextDouble() =>
        (NextUInt64() >> 11) * (1.0 / (1UL << 53));
}

public sealed record SimulationEvidenceRecord(
    long Sequence,
    string Category,
    string Name,
    string Value,
    DateTimeOffset OccurredAtUtc);

public sealed record SimulationEvidenceEnvelope(
    string ScenarioId,
    string ScenarioVersion,
    string ScenarioManifestHash,
    long Seed,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    IReadOnlyList<SimulationEvidenceRecord> Records,
    string EvidenceHash)
{
    public static SimulationEvidenceEnvelope Create(
        RegisteredSimulationScenario scenario,
        DateTimeOffset startedAtUtc,
        DateTimeOffset finishedAtUtc,
        IEnumerable<SimulationEvidenceRecord> records,
        SimulationGovernanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (finishedAtUtc < startedAtUtc)
            throw new InvalidOperationException("Simulation evidence finish time cannot predate start time.");

        var materialized = records.OrderBy(static item => item.Sequence).ToArray();
        if (materialized.Length > options.MaximumEvidenceRecords)
            throw new InvalidOperationException("Simulation evidence exceeds MaximumEvidenceRecords.");
        if (materialized.Select(static item => item.Sequence).Distinct().Count() != materialized.Length)
            throw new InvalidOperationException("Simulation evidence sequence numbers must be unique.");

        var canonical = JsonSerializer.Serialize(new
        {
            scenario.ScenarioId,
            ScenarioVersion = scenario.Version,
            scenario.ManifestHash,
            scenario.Seed,
            StartedAtUtc = startedAtUtc.ToUniversalTime(),
            FinishedAtUtc = finishedAtUtc.ToUniversalTime(),
            Records = materialized.Select(static item => new
            {
                item.Sequence,
                item.Category,
                item.Name,
                item.Value,
                OccurredAtUtc = item.OccurredAtUtc.ToUniversalTime()
            })
        });

        return new SimulationEvidenceEnvelope(
            scenario.ScenarioId,
            scenario.Version,
            scenario.ManifestHash,
            scenario.Seed,
            startedAtUtc,
            finishedAtUtc,
            materialized,
            SimulationScenarioValidator.ComputeSha256(Encoding.UTF8.GetBytes(canonical)));
    }
}
