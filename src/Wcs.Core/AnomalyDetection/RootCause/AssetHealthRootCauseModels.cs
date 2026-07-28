namespace Wcs.Core.AnomalyDetection.RootCause;

using Wcs.Core.AnomalyDetection.HealthGovernance;

public enum RootCauseNodeKind
{
    Asset = 0,
    Component = 1,
    Signal = 2,
    Task = 3,
    Station = 4,
    Segment = 5
}

public enum RootCauseRelationType
{
    DependsOn = 0,
    Feeds = 1,
    Controls = 2,
    LocatedAt = 3,
    Carries = 4
}

public enum RootCausePropagationRole
{
    RootCause = 0,
    Intermediate = 1,
    Symptom = 2
}

public enum RootCauseReviewDecision
{
    Pending = 0,
    Confirmed = 1,
    Rejected = 2,
    Supplemented = 3
}

public sealed class AssetHealthRootCauseOptions
{
    public bool Enabled { get; set; }
    public int EvaluationIntervalSeconds { get; set; } = 10;
    public int CorrelationWindowSeconds { get; set; } = 300;
    public int MaximumPropagationDepth { get; set; } = 6;
    public int MaximumGraphNodes { get; set; } = 20_000;
    public int MaximumGraphEdges { get; set; } = 50_000;
    public int MaximumEventsPerAnalysis { get; set; } = 200;
    public int MaximumCandidates { get; set; } = 10;
    public int MaximumPaths { get; set; } = 100;
    public int MaximumAnalysesQueryCount { get; set; } = 1_000;
    public double MinimumCandidateConfidence { get; set; } = 0.25;
    public bool AllowCycles { get; set; }
    public int AnalysisRetentionHours { get; set; } = 2_160;
    public int MaintenanceIntervalSeconds { get; set; } = 3_600;
    public int MaintenanceBatchSize { get; set; } = 2_000;
    public RootCauseGraphDefinition Graph { get; set; } = new();
}

public sealed class RootCauseGraphDefinition
{
    public string Version { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public List<RootCauseGraphNode> Nodes { get; set; } = new();
    public List<RootCauseGraphEdge> Edges { get; set; } = new();
}

public sealed class RootCauseGraphNode
{
    public string NodeId { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public RootCauseNodeKind Kind { get; set; } = RootCauseNodeKind.Asset;
}

public sealed class RootCauseGraphEdge
{
    public string EdgeId { get; set; } = string.Empty;
    public string UpstreamNodeId { get; set; } = string.Empty;
    public string DownstreamNodeId { get; set; } = string.Empty;
    public RootCauseRelationType RelationType { get; set; } = RootCauseRelationType.DependsOn;
    public double Weight { get; set; } = 1;
    public string? Description { get; set; }
}

public sealed record RootCauseGraphRegistration
{
    public required string Version { get; init; }
    public required string GraphHash { get; init; }
    public required string Source { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTime ApprovedAtUtc { get; init; }
    public required DateTime RegisteredAtUtc { get; init; }
    public required int NodeCount { get; init; }
    public required int EdgeCount { get; init; }
    public required string GraphJson { get; init; }
}

public sealed record RootCausePropagationNode
{
    public required string NodeId { get; init; }
    public required string EntityId { get; init; }
    public required string DisplayName { get; init; }
    public required RootCauseNodeKind Kind { get; init; }
    public required RootCausePropagationRole Role { get; init; }
    public required int Depth { get; init; }
}

public sealed record RootCausePropagationEdge
{
    public required string EdgeId { get; init; }
    public required string UpstreamNodeId { get; init; }
    public required string DownstreamNodeId { get; init; }
    public required RootCauseRelationType RelationType { get; init; }
    public required double Weight { get; init; }
    public string? Description { get; init; }
}

public sealed record RootCausePropagationPath
{
    public required string TargetEventId { get; init; }
    public required string TargetNodeId { get; init; }
    public required int Depth { get; init; }
    public required double PathWeight { get; init; }
    public required IReadOnlyList<RootCausePropagationNode> Nodes { get; init; }
    public required IReadOnlyList<RootCausePropagationEdge> Edges { get; init; }
}

public sealed record RootCauseCandidate
{
    public required string NodeId { get; init; }
    public required string EntityId { get; init; }
    public required string DisplayName { get; init; }
    public required RootCauseNodeKind Kind { get; init; }
    public required double Confidence { get; init; }
    public required double CoverageScore { get; init; }
    public required double TopologyScore { get; init; }
    public required double TemporalScore { get; init; }
    public required double SeverityScore { get; init; }
    public required int SupportingEventCount { get; init; }
    public required IReadOnlyList<string> SupportingEventIds { get; init; }
    public required IReadOnlyList<RootCausePropagationPath> PropagationPaths { get; init; }
    public required string Explanation { get; init; }
}

public sealed record AssetHealthRootCauseAnalysisSnapshot
{
    public required string AnalysisId { get; init; }
    public required string TriggerEventId { get; init; }
    public required int TriggerEventVersion { get; init; }
    public required string TriggerAssetId { get; init; }
    public required string GraphVersion { get; init; }
    public required string GraphHash { get; init; }
    public required DateTime WindowStartUtc { get; init; }
    public required DateTime WindowEndUtc { get; init; }
    public required DateTime AnalyzedAtUtc { get; init; }
    public required int ObservedEventCount { get; init; }
    public required IReadOnlyList<string> ObservedEventIds { get; init; }
    public required IReadOnlyList<RootCauseCandidate> Candidates { get; init; }
    public RootCauseCandidate? PrimaryCandidate { get; init; }
    public required RootCauseReviewDecision ReviewDecision { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTime? ReviewedAtUtc { get; init; }
    public string? ReviewNote { get; init; }
    public string? SelectedRootCauseNodeId { get; init; }
}

public sealed record AssetHealthRootCauseReview
{
    public required string ReviewId { get; init; }
    public required string AnalysisId { get; init; }
    public required RootCauseReviewDecision Decision { get; init; }
    public string? SelectedRootCauseNodeId { get; init; }
    public required string Actor { get; init; }
    public string? Note { get; init; }
    public required DateTime OccurredAtUtc { get; init; }
}

public sealed record AssetHealthRootCauseStatus
{
    public required bool Enabled { get; init; }
    public required bool GovernanceEnabled { get; init; }
    public required bool GraphValid { get; init; }
    public required string GraphVersion { get; init; }
    public required string GraphHash { get; init; }
    public required int GraphNodes { get; init; }
    public required int GraphEdges { get; init; }
    public required bool AllowCycles { get; init; }
    public required int CorrelationWindowSeconds { get; init; }
    public required int MaximumPropagationDepth { get; init; }
    public required int MaximumCandidates { get; init; }
    public DateTime? LastAnalysisUtc { get; init; }
    public string? LastError { get; init; }
}

public sealed record AssetHealthRootCauseStoreStatus
{
    public required bool Enabled { get; init; }
    public required string Provider { get; init; }
    public required bool IsAvailable { get; init; }
    public required int RegisteredGraphs { get; init; }
    public required int RetainedAnalyses { get; init; }
    public required int RetainedReviews { get; init; }
    public DateTime? LastSuccessfulWriteUtc { get; init; }
    public string? LastError { get; init; }
}

public interface IAssetHealthRootCauseAnalysisEngine
{
    RootCauseGraphRegistration GraphRegistration { get; }
    AssetHealthRootCauseAnalysisSnapshot? Analyze(
        AssetHealthEventSnapshot trigger,
        IReadOnlyList<AssetHealthEventSnapshot> correlatedEvents,
        DateTime utcNow);
}

public interface IAssetHealthRootCauseAnalysisStore
{
    string Provider { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask RegisterGraphAsync(
        RootCauseGraphRegistration registration,
        CancellationToken cancellationToken = default);
    ValueTask<bool> SaveAsync(
        AssetHealthRootCauseAnalysisSnapshot analysis,
        CancellationToken cancellationToken = default);
    ValueTask<AssetHealthRootCauseAnalysisSnapshot?> GetAsync(
        string analysisId,
        CancellationToken cancellationToken = default);
    ValueTask<AssetHealthRootCauseAnalysisSnapshot?> GetLatestForTriggerAsync(
        string triggerEventId,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AssetHealthRootCauseAnalysisSnapshot>> GetAnalysesAsync(
        string? triggerEventId = null,
        RootCauseReviewDecision? reviewDecision = null,
        int maximumCount = 200,
        CancellationToken cancellationToken = default);
    ValueTask<AssetHealthRootCauseAnalysisSnapshot?> AppendReviewAsync(
        AssetHealthRootCauseReview review,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AssetHealthRootCauseReview>> GetReviewsAsync(
        string analysisId,
        int maximumCount = 200,
        CancellationToken cancellationToken = default);
    ValueTask<AssetHealthRootCauseStoreStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);
    ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}
