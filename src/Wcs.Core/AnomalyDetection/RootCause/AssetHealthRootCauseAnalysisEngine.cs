namespace Wcs.Core.AnomalyDetection.RootCause;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 基于已审批依赖图、健康事件时间顺序和传播覆盖率生成可解释根因候选。
/// 分析只读，不写 PLC、不停止设备，也不改变任务、路线、路权或调度。
/// </summary>
public sealed class AssetHealthRootCauseAnalysisEngine : IAssetHealthRootCauseAnalysisEngine
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AssetHealthRootCauseOptions _options;
    private readonly RootCauseGraphIndex _graph;

    public AssetHealthRootCauseAnalysisEngine(AssetHealthRootCauseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _graph = RootCauseGraphIndex.Create(options);
        GraphRegistration = CreateRegistration(options.Graph, _graph.GraphHash);
    }

    public RootCauseGraphRegistration GraphRegistration { get; }

    public AssetHealthRootCauseAnalysisSnapshot? Analyze(
        AssetHealthEventSnapshot trigger,
        IReadOnlyList<AssetHealthEventSnapshot> correlatedEvents,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(correlatedEvents);
        if (!_options.Enabled || trigger.LifecycleStatus != AssetHealthEventLifecycleStatus.Active)
            return null;
        if (!_graph.TryGetNodeByEntity(trigger.AssetId, out _))
            return null;

        utcNow = NormalizeUtc(utcNow);
        var window = TimeSpan.FromSeconds(_options.CorrelationWindowSeconds);
        var windowStart = trigger.FirstDetectedUtc - window;
        var windowEnd = trigger.LastObservedUtc + window;
        var events = correlatedEvents
            .Append(trigger)
            .Where(static item => item.LifecycleStatus == AssetHealthEventLifecycleStatus.Active)
            .Where(item => item.LastObservedUtc >= windowStart && item.FirstDetectedUtc <= windowEnd)
            .GroupBy(static item => item.EventId, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static item => item.Version)
                .ThenByDescending(static item => item.LastObservedUtc)
                .First())
            .OrderByDescending(item => string.Equals(item.EventId, trigger.EventId, StringComparison.Ordinal))
            .ThenByDescending(static item => item.LastObservedUtc)
            .ThenBy(static item => item.EventId, StringComparer.Ordinal)
            .Take(_options.MaximumEventsPerAnalysis)
            .ToArray();

        var observed = events
            .Select(item => _graph.TryGetNodeByEntity(item.AssetId, out var node)
                ? new ObservedEvent(item, node)
                : null)
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();
        if (observed.Length == 0 || observed.All(item => item.Event.EventId != trigger.EventId))
            return null;

        var candidateNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in observed)
        {
            candidateNodeIds.Add(item.Node.NodeId);
            AddUpstreamCandidates(item.Node.NodeId, candidateNodeIds);
        }

        var candidates = candidateNodeIds
            .Select(nodeId => CreateCandidate(nodeId, observed))
            .Where(static candidate => candidate is not null)
            .Select(static candidate => candidate!)
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence)
            .OrderByDescending(static candidate => candidate.Confidence)
            .ThenByDescending(static candidate => candidate.SupportingEventCount)
            .ThenBy(static candidate => candidate.NodeId, StringComparer.Ordinal)
            .Take(_options.MaximumCandidates)
            .ToArray();

        var observedIds = observed
            .Select(static item => item.Event.EventId)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        var analysisId = CreateAnalysisId(trigger, observed, _graph.GraphHash);
        return new AssetHealthRootCauseAnalysisSnapshot
        {
            AnalysisId = analysisId,
            TriggerEventId = trigger.EventId,
            TriggerEventVersion = trigger.Version,
            TriggerAssetId = trigger.AssetId,
            GraphVersion = _options.Graph.Version,
            GraphHash = _graph.GraphHash,
            WindowStartUtc = observed.Min(static item => item.Event.FirstDetectedUtc),
            WindowEndUtc = observed.Max(static item => item.Event.LastObservedUtc),
            AnalyzedAtUtc = utcNow,
            ObservedEventCount = observed.Length,
            ObservedEventIds = observedIds,
            Candidates = candidates,
            PrimaryCandidate = candidates.FirstOrDefault(),
            ReviewDecision = RootCauseReviewDecision.Pending
        };
    }

    private void AddUpstreamCandidates(string startNodeId, HashSet<string> candidates)
    {
        var queue = new Queue<(string NodeId, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { startNodeId };
        queue.Enqueue((startNodeId, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth >= _options.MaximumPropagationDepth) continue;
            foreach (var edge in _graph.GetIncoming(current.NodeId))
            {
                if (!visited.Add(edge.UpstreamNodeId)) continue;
                candidates.Add(edge.UpstreamNodeId);
                queue.Enqueue((edge.UpstreamNodeId, current.Depth + 1));
            }
        }
    }

    private RootCauseCandidate? CreateCandidate(
        string candidateNodeId,
        IReadOnlyList<ObservedEvent> observed)
    {
        var paths = new List<(ObservedEvent Observation, RootCausePropagationPath Path)>();
        foreach (var observation in observed)
        {
            var path = FindPath(candidateNodeId, observation.Node.NodeId, observation.Event.EventId);
            if (path is not null) paths.Add((observation, path));
            if (paths.Count >= _options.MaximumPaths) break;
        }
        if (paths.Count == 0) return null;

        var node = _graph.GetNode(candidateNodeId);
        var directEvents = observed
            .Where(item => item.Node.NodeId == candidateNodeId)
            .Select(static item => item.Event)
            .ToArray();
        var coverage = (double)paths.Count / observed.Count;
        var topology = paths.Average(static item =>
            (1d / (1 + item.Path.Depth)) * item.Path.PathWeight);
        var temporal = CalculateTemporalScore(directEvents, paths);
        var severity = directEvents.Length > 0
            ? directEvents.Max(static item => GradeSeverity(item.Grade))
            : paths.Max(static item => GradeSeverity(item.Observation.Event.Grade)) * 0.5;
        var confidence = Math.Clamp(
            (coverage * 0.40) +
            (topology * 0.25) +
            (temporal * 0.20) +
            (severity * 0.15),
            0,
            1);
        confidence = Round(confidence);

        var supportingIds = paths
            .Select(static item => item.Observation.Event.EventId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        return new RootCauseCandidate
        {
            NodeId = node.NodeId,
            EntityId = node.EntityId,
            DisplayName = node.DisplayName,
            Kind = node.Kind,
            Confidence = confidence,
            CoverageScore = Round(coverage),
            TopologyScore = Round(topology),
            TemporalScore = Round(temporal),
            SeverityScore = Round(severity),
            SupportingEventCount = supportingIds.Length,
            SupportingEventIds = supportingIds,
            PropagationPaths = paths.Select(static item => item.Path).ToArray(),
            Explanation = $"Candidate {node.DisplayName} covers {supportingIds.Length}/{observed.Count} active events; " +
                          $"topology={Round(topology):F4}, temporal={Round(temporal):F4}, severity={Round(severity):F4}."
        };
    }

    private double CalculateTemporalScore(
        IReadOnlyList<AssetHealthEventSnapshot> directEvents,
        IReadOnlyList<(ObservedEvent Observation, RootCausePropagationPath Path)> paths)
    {
        if (directEvents.Count == 0) return 0.35;
        var candidateTime = directEvents.Min(static item => item.FirstDetectedUtc);
        var windowSeconds = Math.Max(1, _options.CorrelationWindowSeconds);
        return paths.Average(item =>
        {
            var delta = (item.Observation.Event.FirstDetectedUtc - candidateTime).TotalSeconds;
            if (delta >= 0)
                return 0.6 + (0.4 * Math.Min(delta / windowSeconds, 1));
            return Math.Max(0, 0.6 - (0.6 * Math.Min(Math.Abs(delta) / windowSeconds, 1)));
        });
    }

    private RootCausePropagationPath? FindPath(
        string sourceNodeId,
        string targetNodeId,
        string targetEventId)
    {
        if (sourceNodeId == targetNodeId)
        {
            var node = _graph.GetNode(sourceNodeId);
            return new RootCausePropagationPath
            {
                TargetEventId = targetEventId,
                TargetNodeId = targetNodeId,
                Depth = 0,
                PathWeight = 1,
                Nodes = new[] { ToPathNode(node, RootCausePropagationRole.RootCause, 0) },
                Edges = Array.Empty<RootCausePropagationEdge>()
            };
        }

        var queue = new Queue<(string NodeId, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceNodeId };
        var parents = new Dictionary<string, (string ParentNodeId, RootCauseGraphEdge Edge)>(StringComparer.Ordinal);
        queue.Enqueue((sourceNodeId, 0));
        var found = false;
        while (queue.Count > 0 && !found)
        {
            var current = queue.Dequeue();
            if (current.Depth >= _options.MaximumPropagationDepth) continue;
            foreach (var edge in _graph.GetOutgoing(current.NodeId))
            {
                if (!visited.Add(edge.DownstreamNodeId)) continue;
                parents[edge.DownstreamNodeId] = (current.NodeId, edge);
                if (edge.DownstreamNodeId == targetNodeId)
                {
                    found = true;
                    break;
                }
                queue.Enqueue((edge.DownstreamNodeId, current.Depth + 1));
            }
        }
        if (!found) return null;

        var nodeIds = new List<string> { targetNodeId };
        var edges = new List<RootCauseGraphEdge>();
        var cursor = targetNodeId;
        while (cursor != sourceNodeId)
        {
            var parent = parents[cursor];
            edges.Add(parent.Edge);
            cursor = parent.ParentNodeId;
            nodeIds.Add(cursor);
        }
        nodeIds.Reverse();
        edges.Reverse();
        var pathNodes = nodeIds.Select((nodeId, index) =>
        {
            var role = index == 0
                ? RootCausePropagationRole.RootCause
                : index == nodeIds.Count - 1
                    ? RootCausePropagationRole.Symptom
                    : RootCausePropagationRole.Intermediate;
            return ToPathNode(_graph.GetNode(nodeId), role, index);
        }).ToArray();
        var pathEdges = edges.Select(static edge => new RootCausePropagationEdge
        {
            EdgeId = edge.EdgeId,
            UpstreamNodeId = edge.UpstreamNodeId,
            DownstreamNodeId = edge.DownstreamNodeId,
            RelationType = edge.RelationType,
            Weight = edge.Weight,
            Description = edge.Description
        }).ToArray();
        return new RootCausePropagationPath
        {
            TargetEventId = targetEventId,
            TargetNodeId = targetNodeId,
            Depth = edges.Count,
            PathWeight = Round(edges.Aggregate(1d, static (value, edge) => value * edge.Weight)),
            Nodes = pathNodes,
            Edges = pathEdges
        };
    }

    private static RootCausePropagationNode ToPathNode(
        RootCauseGraphNode node,
        RootCausePropagationRole role,
        int depth) => new()
    {
        NodeId = node.NodeId,
        EntityId = node.EntityId,
        DisplayName = node.DisplayName,
        Kind = node.Kind,
        Role = role,
        Depth = depth
    };

    private static double GradeSeverity(AssetHealthGrade grade) => grade switch
    {
        AssetHealthGrade.Healthy => 0,
        AssetHealthGrade.Attention => 0.33,
        AssetHealthGrade.Degraded => 0.66,
        AssetHealthGrade.Critical => 1,
        _ => 0
    };

    private static string CreateAnalysisId(
        AssetHealthEventSnapshot trigger,
        IReadOnlyList<ObservedEvent> observed,
        string graphHash)
    {
        var eventVersions = string.Join(',', observed
            .OrderBy(static item => item.Event.EventId, StringComparer.Ordinal)
            .Select(static item => $"{item.Event.EventId}:{item.Event.Version}"));
        var raw = $"{trigger.EventId}|{trigger.Version}|{graphHash}|{eventVersions}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static RootCauseGraphRegistration CreateRegistration(
        RootCauseGraphDefinition definition,
        string graphHash) => new()
    {
        Version = definition.Version.Trim(),
        GraphHash = graphHash,
        Source = definition.Source.Trim(),
        ApprovedBy = definition.ApprovedBy.Trim(),
        ApprovedAtUtc = NormalizeUtc(definition.ApprovedAtUtc!.Value),
        RegisteredAtUtc = DateTime.UtcNow,
        NodeCount = definition.Nodes.Count,
        EdgeCount = definition.Edges.Count,
        GraphJson = JsonSerializer.Serialize(definition, JsonOptions)
    };

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static double Round(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record ObservedEvent(
        AssetHealthEventSnapshot Event,
        RootCauseGraphNode Node);

    private sealed class RootCauseGraphIndex
    {
        private readonly Dictionary<string, RootCauseGraphNode> _nodes;
        private readonly Dictionary<string, RootCauseGraphNode> _nodesByEntity;
        private readonly Dictionary<string, RootCauseGraphEdge[]> _outgoing;
        private readonly Dictionary<string, RootCauseGraphEdge[]> _incoming;

        private RootCauseGraphIndex(
            Dictionary<string, RootCauseGraphNode> nodes,
            Dictionary<string, RootCauseGraphNode> nodesByEntity,
            Dictionary<string, RootCauseGraphEdge[]> outgoing,
            Dictionary<string, RootCauseGraphEdge[]> incoming,
            string graphHash)
        {
            _nodes = nodes;
            _nodesByEntity = nodesByEntity;
            _outgoing = outgoing;
            _incoming = incoming;
            GraphHash = graphHash;
        }

        public string GraphHash { get; }

        public static RootCauseGraphIndex Create(AssetHealthRootCauseOptions options)
        {
            var definition = options.Graph ?? throw new InvalidOperationException("AssetHealthRootCause:Graph is required.");
            if (!options.Enabled)
            {
                definition.Version = string.IsNullOrWhiteSpace(definition.Version) ? "disabled" : definition.Version.Trim();
                definition.Source = string.IsNullOrWhiteSpace(definition.Source) ? "disabled" : definition.Source.Trim();
                definition.ApprovedBy = string.IsNullOrWhiteSpace(definition.ApprovedBy) ? "disabled" : definition.ApprovedBy.Trim();
                definition.ApprovedAtUtc ??= DateTime.UnixEpoch;
            }
            if (string.IsNullOrWhiteSpace(definition.Version))
                throw new InvalidOperationException("AssetHealthRootCause:Graph:Version is required when enabled.");
            if (string.IsNullOrWhiteSpace(definition.Source))
                throw new InvalidOperationException("AssetHealthRootCause:Graph:Source is required when enabled.");
            if (string.IsNullOrWhiteSpace(definition.ApprovedBy) || definition.ApprovedAtUtc is null)
                throw new InvalidOperationException("AssetHealthRootCause graph approval information is required when enabled.");
            if (definition.Nodes.Count > options.MaximumGraphNodes)
                throw new InvalidOperationException("AssetHealthRootCause graph node capacity exceeded.");
            if (definition.Edges.Count > options.MaximumGraphEdges)
                throw new InvalidOperationException("AssetHealthRootCause graph edge capacity exceeded.");
            if (options.Enabled && definition.Nodes.Count == 0)
                throw new InvalidOperationException("AssetHealthRootCause graph must contain at least one node when enabled.");

            var nodes = new Dictionary<string, RootCauseGraphNode>(StringComparer.Ordinal);
            var nodesByEntity = new Dictionary<string, RootCauseGraphNode>(StringComparer.Ordinal);
            foreach (var node in definition.Nodes)
            {
                node.NodeId = node.NodeId?.Trim() ?? string.Empty;
                node.EntityId = node.EntityId?.Trim() ?? string.Empty;
                node.DisplayName = string.IsNullOrWhiteSpace(node.DisplayName) ? node.EntityId : node.DisplayName.Trim();
                if (node.NodeId.Length == 0 || node.EntityId.Length == 0)
                    throw new InvalidOperationException("Root cause graph node NodeId and EntityId are required.");
                if (!Enum.IsDefined(node.Kind))
                    throw new InvalidOperationException($"Root cause graph node kind is invalid: {node.NodeId}.");
                if (!nodes.TryAdd(node.NodeId, node))
                    throw new InvalidOperationException($"Duplicate root cause graph NodeId: {node.NodeId}.");
                if (!nodesByEntity.TryAdd(node.EntityId, node))
                    throw new InvalidOperationException($"Duplicate root cause graph EntityId: {node.EntityId}.");
            }

            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in definition.Edges)
            {
                edge.EdgeId = edge.EdgeId?.Trim() ?? string.Empty;
                edge.UpstreamNodeId = edge.UpstreamNodeId?.Trim() ?? string.Empty;
                edge.DownstreamNodeId = edge.DownstreamNodeId?.Trim() ?? string.Empty;
                edge.Description = string.IsNullOrWhiteSpace(edge.Description) ? null : edge.Description.Trim();
                if (edge.EdgeId.Length == 0 || !edgeIds.Add(edge.EdgeId))
                    throw new InvalidOperationException($"Duplicate or empty root cause graph EdgeId: {edge.EdgeId}.");
                if (!nodes.ContainsKey(edge.UpstreamNodeId) || !nodes.ContainsKey(edge.DownstreamNodeId))
                    throw new InvalidOperationException($"Root cause graph edge references an unknown node: {edge.EdgeId}.");
                if (edge.UpstreamNodeId == edge.DownstreamNodeId)
                    throw new InvalidOperationException($"Root cause graph self edge is not allowed: {edge.EdgeId}.");
                if (!Enum.IsDefined(edge.RelationType))
                    throw new InvalidOperationException($"Root cause graph relation is invalid: {edge.EdgeId}.");
                if (!double.IsFinite(edge.Weight) || edge.Weight <= 0 || edge.Weight > 1)
                    throw new InvalidOperationException($"Root cause graph edge weight must be in (0,1]: {edge.EdgeId}.");
            }

            if (!options.AllowCycles && HasCycle(nodes.Keys, definition.Edges))
                throw new InvalidOperationException("AssetHealthRootCause graph contains a cycle while AllowCycles=false.");

            var outgoing = definition.Edges
                .GroupBy(static edge => edge.UpstreamNodeId, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.OrderBy(static edge => edge.EdgeId, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
            var incoming = definition.Edges
                .GroupBy(static edge => edge.DownstreamNodeId, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.OrderBy(static edge => edge.EdgeId, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
            return new RootCauseGraphIndex(
                nodes,
                nodesByEntity,
                outgoing,
                incoming,
                CalculateGraphHash(definition));
        }

        public bool TryGetNodeByEntity(string entityId, out RootCauseGraphNode node) =>
            _nodesByEntity.TryGetValue(entityId?.Trim() ?? string.Empty, out node!);

        public RootCauseGraphNode GetNode(string nodeId) => _nodes[nodeId];

        public IReadOnlyList<RootCauseGraphEdge> GetOutgoing(string nodeId) =>
            _outgoing.TryGetValue(nodeId, out var edges) ? edges : Array.Empty<RootCauseGraphEdge>();

        public IReadOnlyList<RootCauseGraphEdge> GetIncoming(string nodeId) =>
            _incoming.TryGetValue(nodeId, out var edges) ? edges : Array.Empty<RootCauseGraphEdge>();

        private static bool HasCycle(
            IEnumerable<string> nodeIds,
            IReadOnlyList<RootCauseGraphEdge> edges)
        {
            var indegree = nodeIds.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
            foreach (var edge in edges) indegree[edge.DownstreamNodeId]++;
            var queue = new Queue<string>(indegree
                .Where(static pair => pair.Value == 0)
                .Select(static pair => pair.Key)
                .OrderBy(static id => id, StringComparer.Ordinal));
            var visited = 0;
            var outgoing = edges
                .GroupBy(static edge => edge.UpstreamNodeId, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                var nodeId = queue.Dequeue();
                visited++;
                if (!outgoing.TryGetValue(nodeId, out var next)) continue;
                foreach (var edge in next)
                {
                    indegree[edge.DownstreamNodeId]--;
                    if (indegree[edge.DownstreamNodeId] == 0)
                        queue.Enqueue(edge.DownstreamNodeId);
                }
            }
            return visited != indegree.Count;
        }

        private static string CalculateGraphHash(RootCauseGraphDefinition definition)
        {
            var builder = new StringBuilder();
            builder.Append(definition.Version.Trim()).Append('|')
                .Append(definition.Source.Trim()).Append('|')
                .Append(definition.ApprovedBy.Trim()).Append('|')
                .Append(definition.ApprovedAtUtc!.Value.ToUniversalTime().Ticks);
            foreach (var node in definition.Nodes.OrderBy(static item => item.NodeId, StringComparer.Ordinal))
            {
                builder.Append("|N:").Append(node.NodeId).Append(':').Append(node.EntityId)
                    .Append(':').Append((int)node.Kind).Append(':').Append(node.DisplayName);
            }
            foreach (var edge in definition.Edges.OrderBy(static item => item.EdgeId, StringComparer.Ordinal))
            {
                builder.Append("|E:").Append(edge.EdgeId).Append(':').Append(edge.UpstreamNodeId)
                    .Append(':').Append(edge.DownstreamNodeId).Append(':').Append((int)edge.RelationType)
                    .Append(':').Append(edge.Weight.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(':').Append(edge.Description);
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }
}
