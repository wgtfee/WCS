using System.Security.Cryptography;
using System.Text;

namespace Wcs.DecisionIntelligence;

public enum DecisionProposalStatus { Shadow, Blocked, PendingApproval, Approved, Rejected, Expired, OutcomeRecorded }
public enum ProposalType { MaintenanceWindowRecommendation, AssetLoadReductionRecommendation, VehicleSelectionRecommendation, TaskPriorityRecommendation, StandbyAssetRecommendation, InspectionRecommendation }

public sealed record DecisionEvidence(string ModelId, string ModelVersion, string FeatureSnapshotId, string FeatureSchemaHash, string EvidenceHash);
public sealed record DecisionCandidate(string CandidateId, string Action, decimal Score, IReadOnlyDictionary<string,string> Parameters);
public sealed record ConstraintResult(string Code, bool Passed, string Reason, string EvidenceHash);
public sealed record DecisionExplanation(string Summary, IReadOnlyList<string> Factors, string EvidenceHash);
public sealed record DecisionProposal(
    string ProposalId, ProposalType Type, DecisionProposalStatus Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc, string CorrelationId, string IdempotencyKey, DecisionCandidate Candidate,
    DecisionEvidence Evidence, DecisionExplanation Explanation, IReadOnlyList<ConstraintResult> Constraints)
{
    public bool ControlWriteAllowed => false;
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;
}

public sealed record DecisionContext(string CorrelationId, string IdempotencyKey, ProposalType Type,
    string? FeatureSnapshotId, string? ChampionModelId, string? ChampionModelVersion,
    string FeatureSchemaHash, DateTimeOffset AsOfUtc, DateTimeOffset ExpiresAtUtc);

public sealed record DecisionProposalResult(bool Generated, string Code, DecisionProposal? Proposal)
{
    public static DecisionProposalResult MissingSnapshot() => new(false, "FeatureSnapshotRequired", null);
    public static DecisionProposalResult ModelUnavailable() => new(false, "ModelUnavailable", null);
}

public interface IDecisionConstraintEvaluator
{
    Task<IReadOnlyList<ConstraintResult>> EvaluateAsync(DecisionContext context, DecisionCandidate candidate, CancellationToken ct);
}

public interface IDecisionProposalEngine
{
    Task<DecisionProposalResult> EvaluateAsync(DecisionContext context, CancellationToken ct);
}

public static class DecisionHash
{
    public static string Sha256(params string[] values)
    {
        var canonical = string.Join("\n", values.Select(v => v ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
