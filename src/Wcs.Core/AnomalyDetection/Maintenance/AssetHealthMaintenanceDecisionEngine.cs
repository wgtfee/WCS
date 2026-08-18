namespace Wcs.Core.AnomalyDetection.Maintenance;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.RootCause;

public sealed class AssetHealthMaintenanceDecisionEngine : IAssetHealthMaintenanceDecisionEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AssetHealthMaintenanceOptions _options;
    private readonly IReadOnlyList<MaintenanceDecisionRule> _rules;

    public AssetHealthMaintenanceDecisionEngine(AssetHealthMaintenanceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.RuleSet ??= new MaintenanceRuleSetDefinition();
        _options.RuleSet.Rules ??= new List<MaintenanceDecisionRule>();

        var normalized = NormalizeAndValidate(_options);
        _rules = normalized.Rules;
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var hash = Sha256(json);

        RuleSetRegistration = new MaintenanceRuleSetRegistration
        {
            Version = normalized.Version,
            RuleSetHash = hash,
            Source = normalized.Source,
            ApprovedBy = normalized.ApprovedBy,
            ApprovedAtUtc = normalized.ApprovedAtUtc ?? DateTime.UnixEpoch,
            RegisteredAtUtc = DateTime.UtcNow,
            RuleCount = normalized.Rules.Count,
            RuleSetJson = json
        };
    }

    public MaintenanceRuleSetRegistration RuleSetRegistration { get; }

    public AssetHealthMaintenanceRecommendation? Generate(
        AssetHealthRootCauseAnalysisSnapshot analysis,
        AssetHealthEventSnapshot healthEvent,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(healthEvent);

        if (!_options.Enabled || _rules.Count == 0)
            return null;
        if (healthEvent.LifecycleStatus != AssetHealthEventLifecycleStatus.Active)
            return null;
        if (!string.Equals(analysis.TriggerEventId, healthEvent.EventId, StringComparison.Ordinal))
            return null;
        if (analysis.TriggerEventVersion != healthEvent.Version)
            return null;
        if (analysis.ReviewDecision is not (RootCauseReviewDecision.Confirmed or RootCauseReviewDecision.Supplemented))
            return null;
        if (string.IsNullOrWhiteSpace(analysis.SelectedRootCauseNodeId))
            return null;

        var selectedNodeId = analysis.SelectedRootCauseNodeId.Trim();
        var candidate = analysis.Candidates.FirstOrDefault(item =>
            string.Equals(item.NodeId, selectedNodeId, StringComparison.Ordinal));

        if (analysis.ReviewDecision == RootCauseReviewDecision.Confirmed && candidate is null)
            return null;
        if (analysis.ReviewDecision == RootCauseReviewDecision.Confirmed &&
            candidate!.Confidence < _options.MinimumRootCauseConfidence)
            return null;

        var matchingRules = _rules
            .Where(rule => Matches(rule, selectedNodeId, candidate, healthEvent))
            .OrderByDescending(rule =>
                string.Equals(rule.RootCauseNodeId, selectedNodeId, StringComparison.Ordinal))
            .ThenBy(rule => rule.Priority)
            .ThenBy(rule => rule.RuleId, StringComparer.Ordinal)
            .ToArray();
        var selectedRule = matchingRules.FirstOrDefault();
        if (selectedRule is null)
            return null;

        var rootCauseKind = candidate?.Kind ?? selectedRule.RootCauseKind ?? RootCauseNodeKind.Asset;
        var rootCauseEntityId = candidate?.EntityId ?? selectedNodeId;
        var rootCauseDisplayName = candidate?.DisplayName ?? selectedNodeId;
        var confidence = candidate?.Confidence ?? 0;
        var recommendationId = Sha256(string.Join('|',
            RuleSetRegistration.RuleSetHash,
            analysis.AnalysisId,
            healthEvent.EventId,
            healthEvent.Version,
            selectedRule.RuleId));

        var reviewSource = analysis.ReviewDecision == RootCauseReviewDecision.Confirmed
            ? $"confirmed candidate confidence {confidence:F4}"
            : "human supplemented root cause";

        return new AssetHealthMaintenanceRecommendation
        {
            RecommendationId = recommendationId,
            AnalysisId = analysis.AnalysisId,
            TriggerEventId = healthEvent.EventId,
            TriggerEventVersion = healthEvent.Version,
            AssetId = healthEvent.AssetId,
            RuleSetVersion = RuleSetRegistration.Version,
            RuleSetHash = RuleSetRegistration.RuleSetHash,
            RuleId = selectedRule.RuleId,
            RootCauseNodeId = selectedNodeId,
            RootCauseEntityId = rootCauseEntityId,
            RootCauseDisplayName = rootCauseDisplayName,
            RootCauseKind = rootCauseKind,
            RootCauseConfidence = confidence,
            RootCauseReviewDecision = analysis.ReviewDecision,
            EventGrade = healthEvent.Grade,
            PreMaintenanceHealthScore = healthEvent.HealthScore,
            Title = selectedRule.Title,
            Priority = selectedRule.Priority,
            EstimatedMinutes = selectedRule.EstimatedMinutes,
            InspectionItems = selectedRule.InspectionItems.ToArray(),
            Components = selectedRule.Components.ToArray(),
            Tools = selectedRule.Tools.ToArray(),
            SpareParts = selectedRule.SpareParts.ToArray(),
            SafetyNotes = selectedRule.SafetyNotes.ToArray(),
            Explanation = $"Rule {selectedRule.RuleId} matched reviewed root cause {selectedNodeId}; {reviewSource}. This is an inspection recommendation only.",
            Status = MaintenanceRecommendationStatus.Proposed,
            CreatedAtUtc = utcNow
        };
    }

    private static bool Matches(
        MaintenanceDecisionRule rule,
        string selectedNodeId,
        RootCauseCandidate? candidate,
        AssetHealthEventSnapshot healthEvent)
    {
        if (healthEvent.Grade < rule.MinimumEventGrade)
            return false;
        if (!string.IsNullOrWhiteSpace(rule.RootCauseNodeId) &&
            !string.Equals(rule.RootCauseNodeId, selectedNodeId, StringComparison.Ordinal))
            return false;
        if (rule.RootCauseKind.HasValue && candidate is not null && candidate.Kind != rule.RootCauseKind.Value)
            return false;
        if (rule.RootCauseKind.HasValue && candidate is null &&
            string.IsNullOrWhiteSpace(rule.RootCauseNodeId))
            return false;
        return true;
    }

    private static MaintenanceRuleSetDefinition NormalizeAndValidate(AssetHealthMaintenanceOptions options)
    {
        var source = options.RuleSet;
        var normalized = new MaintenanceRuleSetDefinition
        {
            Version = source.Version?.Trim() ?? string.Empty,
            Source = source.Source?.Trim() ?? string.Empty,
            ApprovedBy = source.ApprovedBy?.Trim() ?? string.Empty,
            ApprovedAtUtc = source.ApprovedAtUtc,
            Rules = source.Rules
                .Select(rule => NormalizeRule(rule, options.MaximumItemsPerRecommendation))
                .OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
                .ToList()
        };

        if (normalized.Rules.Count > options.MaximumRules)
            throw new InvalidOperationException(
                $"Maintenance rule count {normalized.Rules.Count} exceeds MaximumRules {options.MaximumRules}.");

        var duplicateRule = normalized.Rules
            .GroupBy(rule => rule.RuleId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRule is not null)
            throw new InvalidOperationException($"Duplicate maintenance RuleId '{duplicateRule.Key}'.");

        if (options.Enabled)
        {
            if (string.IsNullOrWhiteSpace(normalized.Version))
                throw new InvalidOperationException("AssetHealthMaintenance:RuleSet:Version is required when enabled.");
            if (string.IsNullOrWhiteSpace(normalized.Source))
                throw new InvalidOperationException("AssetHealthMaintenance:RuleSet:Source is required when enabled.");
            if (string.IsNullOrWhiteSpace(normalized.ApprovedBy) || !normalized.ApprovedAtUtc.HasValue)
                throw new InvalidOperationException("AssetHealthMaintenance rule set approval metadata is required when enabled.");
        }

        return normalized;
    }

    private static MaintenanceDecisionRule NormalizeRule(
        MaintenanceDecisionRule rule,
        int maximumItems)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var normalized = new MaintenanceDecisionRule
        {
            RuleId = rule.RuleId?.Trim() ?? string.Empty,
            RootCauseNodeId = NullIfWhiteSpace(rule.RootCauseNodeId),
            RootCauseKind = rule.RootCauseKind,
            MinimumEventGrade = rule.MinimumEventGrade,
            Title = rule.Title?.Trim() ?? string.Empty,
            Priority = Math.Clamp(rule.Priority, 1, 5),
            EstimatedMinutes = Math.Clamp(rule.EstimatedMinutes, 1, 10_080),
            InspectionItems = NormalizeItems(rule.InspectionItems, maximumItems),
            Components = NormalizeItems(rule.Components, maximumItems),
            Tools = NormalizeItems(rule.Tools, maximumItems),
            SpareParts = NormalizeItems(rule.SpareParts, maximumItems),
            SafetyNotes = NormalizeItems(rule.SafetyNotes, maximumItems)
        };

        if (string.IsNullOrWhiteSpace(normalized.RuleId))
            throw new InvalidOperationException("Maintenance RuleId is required.");
        if (string.IsNullOrWhiteSpace(normalized.RootCauseNodeId) && !normalized.RootCauseKind.HasValue)
            throw new InvalidOperationException(
                $"Maintenance rule '{normalized.RuleId}' must match RootCauseNodeId or RootCauseKind.");
        if (string.IsNullOrWhiteSpace(normalized.Title))
            throw new InvalidOperationException($"Maintenance rule '{normalized.RuleId}' Title is required.");
        if (normalized.InspectionItems.Count == 0)
            throw new InvalidOperationException(
                $"Maintenance rule '{normalized.RuleId}' must contain at least one inspection item.");
        if (!Enum.IsDefined(normalized.MinimumEventGrade))
            throw new InvalidOperationException(
                $"Maintenance rule '{normalized.RuleId}' has an invalid MinimumEventGrade.");
        return normalized;
    }

    private static List<string> NormalizeItems(IEnumerable<string>? items, int maximumItems) =>
        (items ?? Array.Empty<string>())
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(maximumItems)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
