namespace Wcs.IndustrialIntelligence.Governance;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

public enum AutomationLevel
{
    L0 = 0,
    L1 = 1,
    L2 = 2,
    L3 = 3,
    L4 = 4
}

public enum IndustrialIntelligenceMode
{
    Disabled = 0,
    ReadOnly = 1,
    Shadow = 2,
    Advisory = 3
}

public enum ModelLifecycleStatus
{
    Draft,
    Candidate,
    Shadow,
    Champion,
    Fallback,
    Quarantined,
    Retired
}

public enum FeatureSchemaStatus
{
    Draft,
    Approved,
    Active,
    Retired
}

public enum DecisionProposalStatus
{
    Proposed,
    Blocked,
    Approved,
    Rejected,
    Expired,
    Observed
}

public enum OptimizationPolicyStatus
{
    Draft,
    Candidate,
    Evaluated,
    Recommended,
    Retired
}

public sealed class IndustrialIntelligenceOptions
{
    public const string SectionName = "IndustrialIntelligence";

    public bool Enabled { get; set; }
    public IndustrialIntelligenceMode Mode { get; set; } = IndustrialIntelligenceMode.ReadOnly;
    public string[] AllowedEnvironments { get; set; } = [];
    public AutomationLevel MaximumAutomationLevel { get; set; } = AutomationLevel.L0;
    public int MaximumPendingProposals { get; set; } = 10_000;
    public int ProposalRetentionDays { get; set; } = 180;
    public int EvidenceRetentionDays { get; set; } = 365;
    public int DefaultInferenceTimeoutMs { get; set; } = 200;
    public long MaximumModelPackageBytes { get; set; } = 268_435_456;
    public int MaximumLoadedModels { get; set; } = 8;
    public int MaximumConcurrentInference { get; set; } = 4;
    public int FeatureSnapshotRetentionDays { get; set; } = 90;
    public int MaximumDatasetRows { get; set; } = 5_000_000;
}

public sealed record IndustrialIntelligenceAccessDecision(
    bool Allowed,
    string Reason,
    AutomationLevel EffectiveMaximumAutomationLevel,
    IndustrialIntelligenceMode EffectiveMode);

public static class IndustrialIntelligenceEnvironmentGuard
{
    public static IndustrialIntelligenceAccessDecision Evaluate(
        string? environmentName,
        IndustrialIntelligenceOptions? options)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
            return Denied("environment name is required");
        if (options is null)
            return Denied("IndustrialIntelligence configuration is missing");
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
            return Denied("Production is fail-closed for IDI-P0");
        if (!options.Enabled)
            return Denied("IndustrialIntelligence is disabled");
        if (options.Mode is IndustrialIntelligenceMode.Disabled)
            return Denied("IndustrialIntelligence mode is Disabled");
        if (options.MaximumAutomationLevel > AutomationLevel.L1)
            return Denied("IDI-P0 permits only L0/L1 software-side capability");

        var validation = ValidateBounds(options);
        if (validation is not null)
            return Denied(validation);

        var allowed = options.AllowedEnvironments
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allowed.Any(x => string.Equals(x, "Production", StringComparison.OrdinalIgnoreCase)))
            return Denied("AllowedEnvironments must never include Production");

        if (!allowed.Contains(environmentName.Trim(), StringComparer.OrdinalIgnoreCase))
            return Denied($"environment '{environmentName}' is not approved");

        return new IndustrialIntelligenceAccessDecision(
            true,
            "approved read-only industrial-intelligence environment",
            options.MaximumAutomationLevel,
            options.Mode);
    }

    public static string? ValidateBounds(IndustrialIntelligenceOptions options)
    {
        if (options.MaximumPendingProposals is < 1 or > 100_000)
            return "MaximumPendingProposals must be in [1,100000]";
        if (options.ProposalRetentionDays is < 1 or > 3650)
            return "ProposalRetentionDays must be in [1,3650]";
        if (options.EvidenceRetentionDays is < 1 or > 3650)
            return "EvidenceRetentionDays must be in [1,3650]";
        if (options.DefaultInferenceTimeoutMs is < 10 or > 60_000)
            return "DefaultInferenceTimeoutMs must be in [10,60000]";
        if (options.MaximumModelPackageBytes is < 1_048_576 or > 1_073_741_824)
            return "MaximumModelPackageBytes must be in [1MiB,1GiB]";
        if (options.MaximumLoadedModels is < 1 or > 64)
            return "MaximumLoadedModels must be in [1,64]";
        if (options.MaximumConcurrentInference is < 1 or > 64)
            return "MaximumConcurrentInference must be in [1,64]";
        if (options.FeatureSnapshotRetentionDays is < 1 or > 3650)
            return "FeatureSnapshotRetentionDays must be in [1,3650]";
        if (options.MaximumDatasetRows is < 1 or > 50_000_000)
            return "MaximumDatasetRows must be in [1,50000000]";
        return null;
    }

    private static IndustrialIntelligenceAccessDecision Denied(string reason) =>
        new(false, reason, AutomationLevel.L0, IndustrialIntelligenceMode.ReadOnly);
}

public sealed record EvidenceReference(
    string EvidenceId,
    string EvidenceType,
    string SubjectType,
    string SubjectId,
    string Version,
    string Sha256,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    string CorrelationId)
{
    public static EvidenceReference Create(
        string evidenceId,
        string evidenceType,
        string subjectType,
        string subjectId,
        string version,
        string sha256,
        DateTimeOffset createdAtUtc,
        string createdBy,
        string correlationId)
    {
        Require(evidenceId, nameof(evidenceId));
        Require(evidenceType, nameof(evidenceType));
        Require(subjectType, nameof(subjectType));
        Require(subjectId, nameof(subjectId));
        Require(version, nameof(version));
        Require(createdBy, nameof(createdBy));
        Require(correlationId, nameof(correlationId));
        if (!Hashing.IsSha256(sha256))
            throw new ArgumentException("Sha256 must be a 64-character hexadecimal SHA-256 value.", nameof(sha256));

        return new EvidenceReference(
            evidenceId.Trim(), evidenceType.Trim(), subjectType.Trim(), subjectId.Trim(), version.Trim(),
            sha256.ToLowerInvariant(), createdAtUtc, createdBy.Trim(), correlationId.Trim());
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
    }
}

public sealed record VersionedHashReference(string Version, string Hash)
{
    public static VersionedHashReference Create(string version, string hash)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version is required.", nameof(version));
        if (!Hashing.IsSha256(hash))
            throw new ArgumentException("Hash must be SHA-256.", nameof(hash));
        return new(version.Trim(), hash.ToLowerInvariant());
    }

    public static VersionedHashReference FromCanonicalText(string version, string canonicalText) =>
        Create(version, Hashing.Sha256(canonicalText));
}

public sealed record ActorReason(string Actor, string Reason)
{
    public static ActorReason Create(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Actor is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required.", nameof(reason));
        if (actor.Trim().Length > 200)
            throw new ArgumentOutOfRangeException(nameof(actor));
        if (reason.Trim().Length > 2000)
            throw new ArgumentOutOfRangeException(nameof(reason));
        return new(actor.Trim(), reason.Trim());
    }
}

public sealed record BoundedQuery(int Offset, int Limit)
{
    public const int MaximumLimit = 1000;

    public static BoundedQuery Create(int offset, int limit)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return new(offset, limit);
    }
}

public sealed record IndustrialIntelligenceAuditRecord(
    string AuditId,
    string Action,
    string TargetType,
    string TargetId,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    string? PayloadHash);

public interface IIndustrialIntelligenceAuditJournal
{
    void Append(IndustrialIntelligenceAuditRecord record);
    IReadOnlyList<IndustrialIntelligenceAuditRecord> Snapshot();
}

public sealed class InMemoryIndustrialIntelligenceAuditJournal : IIndustrialIntelligenceAuditJournal
{
    private readonly ConcurrentQueue<IndustrialIntelligenceAuditRecord> _entries = new();
    private readonly ConcurrentDictionary<string, byte> _ids = new(StringComparer.Ordinal);

    public void Append(IndustrialIntelligenceAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = ActorReason.Create(record.Actor, record.Reason);
        if (string.IsNullOrWhiteSpace(record.AuditId) ||
            string.IsNullOrWhiteSpace(record.Action) ||
            string.IsNullOrWhiteSpace(record.TargetType) ||
            string.IsNullOrWhiteSpace(record.TargetId) ||
            string.IsNullOrWhiteSpace(record.CorrelationId))
            throw new ArgumentException("Audit identity fields are required.", nameof(record));
        if (record.PayloadHash is not null && !Hashing.IsSha256(record.PayloadHash))
            throw new ArgumentException("PayloadHash must be SHA-256 when supplied.", nameof(record));
        if (!_ids.TryAdd(record.AuditId, 0))
            throw new InvalidOperationException($"AuditId '{record.AuditId}' already exists; journal entries are immutable.");
        _entries.Enqueue(record);
    }

    public IReadOnlyList<IndustrialIntelligenceAuditRecord> Snapshot() => _entries.ToArray();
}

public static class Hashing
{
    public static string Sha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static ch => char.IsAsciiHexDigit(ch));
}
