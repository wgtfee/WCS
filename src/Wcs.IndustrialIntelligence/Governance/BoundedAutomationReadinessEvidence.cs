namespace Wcs.IndustrialIntelligence.Governance;

using System.Collections.Concurrent;
using System.Globalization;

public sealed record BoundedAutomationReadinessEvidenceRecord(
    string EvaluationId,
    DateTimeOffset EvaluatedAtUtc,
    string EnvironmentName,
    AutomationLevel RequestedLevel,
    string PolicyVersion,
    string PolicyHash,
    string SoftwareHeadSha,
    string SourceEvidenceHash,
    string DecisionHash,
    bool SoftwareSideReady,
    bool ProductionEnablementAllowed,
    string Claim,
    IReadOnlyList<string> Reasons)
{
    public static BoundedAutomationReadinessEvidenceRecord Create(
        string evaluationId,
        DateTimeOffset evaluatedAtUtc,
        BoundedAutomationReadinessRequest request,
        BoundedAutomationReadinessDecision decision)
    {
        if (string.IsNullOrWhiteSpace(evaluationId) || evaluationId.Trim().Length > 80)
            throw new ArgumentException("EvaluationId is required and must be <= 80 characters.", nameof(evaluationId));
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        if (!Hashing.IsSha256(request.AutomationPolicy.PolicyHash))
            throw new ArgumentException("PolicyHash must be SHA-256.", nameof(request));
        if (!BoundedAutomationReadinessGovernance.IsGitCommitSha(request.Evidence.SoftwareHeadSha))
            throw new ArgumentException("SoftwareHeadSha must be a valid Git commit SHA.", nameof(request));
        if (!Hashing.IsSha256(request.Evidence.EvidenceHash))
            throw new ArgumentException("EvidenceHash must be SHA-256.", nameof(request));
        if (decision.ProductionEnablementAllowed)
            throw new InvalidOperationException("P6 evidence cannot record production enablement as allowed.");
        if (!string.Equals(decision.Claim, BoundedAutomationReadinessGovernance.SoftwareOnlyClaim, StringComparison.Ordinal))
            throw new InvalidOperationException("P6 evidence must retain the software-only claim.");

        var reasons = decision.Reasons?.ToArray() ?? Array.Empty<string>();
        var decisionHash = BoundedAutomationReadinessEvidenceHash.Compute(request, decision);
        return new BoundedAutomationReadinessEvidenceRecord(
            evaluationId.Trim(),
            evaluatedAtUtc,
            request.EnvironmentName.Trim(),
            request.AutomationPolicy.RequestedLevel,
            request.AutomationPolicy.PolicyVersion.Trim(),
            request.AutomationPolicy.PolicyHash.ToLowerInvariant(),
            request.Evidence.SoftwareHeadSha.ToLowerInvariant(),
            request.Evidence.EvidenceHash.ToLowerInvariant(),
            decisionHash,
            decision.SoftwareSideReady,
            false,
            BoundedAutomationReadinessGovernance.SoftwareOnlyClaim,
            reasons);
    }
}

public static class BoundedAutomationReadinessEvidenceHash
{
    public static string Compute(
        BoundedAutomationReadinessRequest request,
        BoundedAutomationReadinessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        var prohibited = request.RequestedProhibitedOperations is null
            ? string.Empty
            : string.Join(',', request.RequestedProhibitedOperations.Distinct().OrderBy(static x => x).Select(static x => x.ToString()));
        var reasons = decision.Reasons is null ? string.Empty : string.Join('|', decision.Reasons);
        var canonical = string.Join('\n', new[]
        {
            "schema=wcs-idi-p6-readiness-evidence/v1",
            $"environment={request.EnvironmentName.Trim()}",
            $"policyVersion={request.AutomationPolicy.PolicyVersion.Trim()}",
            $"policyHash={request.AutomationPolicy.PolicyHash.ToLowerInvariant()}",
            $"requestedLevel={(int)request.AutomationPolicy.RequestedLevel}",
            $"executionAllowance={(int)request.ExecutionAllowance.Kind}",
            $"rateLimit={request.RateLimit.MaximumOperationsPerMinute.ToString(CultureInfo.InvariantCulture)}",
            $"budgetLimit={request.BudgetLimit.MaximumCostUnitsPerHour.ToString(CultureInfo.InvariantCulture)}",
            $"maintenanceStart={request.MaintenanceWindow.StartUtc:c}",
            $"maintenanceEnd={request.MaintenanceWindow.EndUtc:c}",
            $"requiredApprovals={request.ApprovalRequirement.RequiredApprovals.ToString(CultureInfo.InvariantCulture)}",
            $"independentSafetyApproval={request.ApprovalRequirement.IndependentSafetyApprovalRequired}",
            $"breakerThreshold={request.CircuitBreaker.FailureThreshold.ToString(CultureInfo.InvariantCulture)}",
            $"breakerOpen={request.CircuitBreaker.OpenDuration:c}",
            $"killSwitchArmed={request.KillSwitch.Armed}",
            $"rollbackTarget={request.RollbackPolicy.TargetVersion?.Trim() ?? string.Empty}",
            $"rollbackDuration={request.RollbackPolicy.MaximumRollbackDuration:c}",
            $"softwareHead={request.Evidence.SoftwareHeadSha.ToLowerInvariant()}",
            $"sourceEvidenceHash={request.Evidence.EvidenceHash.ToLowerInvariant()}",
            $"siteEvidence={request.Evidence.SiteEvidenceValid}",
            $"hilEvidence={request.Evidence.HilEvidenceValid}",
            $"safetyEvidence={request.Evidence.SafetyApprovalEvidenceValid}",
            $"rollbackEvidence={request.Evidence.RollbackEvidenceValid}",
            $"prohibited={prohibited}",
            $"softwareSideReady={decision.SoftwareSideReady}",
            $"productionEnablementAllowed={decision.ProductionEnablementAllowed}",
            $"claim={decision.Claim}",
            $"reasons={reasons}"
        });
        return Hashing.Sha256(canonical);
    }
}

public interface IBoundedAutomationReadinessEvidenceStore
{
    Task AppendAsync(BoundedAutomationReadinessEvidenceRecord record, CancellationToken cancellationToken = default);
    Task<BoundedAutomationReadinessEvidenceRecord?> GetAsync(string evaluationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BoundedAutomationReadinessEvidenceRecord>> ListAsync(int limit, CancellationToken cancellationToken = default);
}

public sealed class InMemoryBoundedAutomationReadinessEvidenceStore : IBoundedAutomationReadinessEvidenceStore
{
    private readonly ConcurrentDictionary<string, BoundedAutomationReadinessEvidenceRecord> _records = new(StringComparer.Ordinal);

    public Task AppendAsync(BoundedAutomationReadinessEvidenceRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(record);
        if (!_records.TryAdd(record.EvaluationId, record))
        {
            var existing = _records[record.EvaluationId];
            if (!string.Equals(existing.DecisionHash, record.DecisionHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("EvaluationId is immutable and already belongs to different readiness evidence.");
        }
        return Task.CompletedTask;
    }

    public Task<BoundedAutomationReadinessEvidenceRecord?> GetAsync(string evaluationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(evaluationId)) return Task.FromResult<BoundedAutomationReadinessEvidenceRecord?>(null);
        _records.TryGetValue(evaluationId.Trim(), out var value);
        return Task.FromResult(value);
    }

    public Task<IReadOnlyList<BoundedAutomationReadinessEvidenceRecord>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        IReadOnlyList<BoundedAutomationReadinessEvidenceRecord> values = _records.Values
            .OrderByDescending(static x => x.EvaluatedAtUtc)
            .ThenBy(static x => x.EvaluationId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(values);
    }

    private static void Validate(BoundedAutomationReadinessEvidenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.EvaluationId) || !Hashing.IsSha256(record.PolicyHash) ||
            !Hashing.IsSha256(record.SourceEvidenceHash) || !Hashing.IsSha256(record.DecisionHash) ||
            !BoundedAutomationReadinessGovernance.IsGitCommitSha(record.SoftwareHeadSha) ||
            record.ProductionEnablementAllowed ||
            !string.Equals(record.Claim, BoundedAutomationReadinessGovernance.SoftwareOnlyClaim, StringComparison.Ordinal))
            throw new InvalidOperationException("P6 readiness evidence violates immutable governance invariants.");
    }
}
