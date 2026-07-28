namespace Wcs.Core.Tests;

using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;
using Wcs.Core.AnomalyDetection.RootCause;

public sealed class AssetHealthRootCauseAnalysisEngineTests
{
    [Fact]
    public void Upstream_event_is_ranked_above_propagated_symptoms()
    {
        var engine = new AssetHealthRootCauseAnalysisEngine(Options());
        var start = DateTime.UnixEpoch;
        var motor = Event("E-MOTOR", "MOTOR-01", 1, AssetHealthGrade.Degraded, start);
        var conveyor = Event("E-CONVEYOR", "CV-01", 1, AssetHealthGrade.Degraded, start.AddSeconds(10));
        var station = Event("E-STATION", "ST-01", 1, AssetHealthGrade.Critical, start.AddSeconds(20));

        var result = engine.Analyze(station, new[] { station, conveyor, motor }, start.AddSeconds(30));

        Assert.NotNull(result);
        Assert.Equal(3, result!.ObservedEventCount);
        Assert.NotNull(result.PrimaryCandidate);
        Assert.Equal("N-MOTOR", result.PrimaryCandidate!.NodeId);
        Assert.Equal(3, result.PrimaryCandidate.SupportingEventCount);
        var stationPath = Assert.Single(result.PrimaryCandidate.PropagationPaths
            .Where(path => path.TargetEventId == station.EventId));
        Assert.Equal(2, stationPath.Depth);
        Assert.Equal(RootCausePropagationRole.RootCause, stationPath.Nodes[0].Role);
        Assert.Equal(RootCausePropagationRole.Intermediate, stationPath.Nodes[1].Role);
        Assert.Equal(RootCausePropagationRole.Symptom, stationPath.Nodes[2].Role);
        Assert.True(result.PrimaryCandidate.Confidence > result.Candidates[1].Confidence);
    }

    [Fact]
    public void Analysis_id_is_deterministic_for_same_graph_and_event_versions()
    {
        var engine = new AssetHealthRootCauseAnalysisEngine(Options());
        var start = DateTime.UnixEpoch;
        var motor = Event("E-MOTOR", "MOTOR-01", 2, AssetHealthGrade.Critical, start);
        var station = Event("E-STATION", "ST-01", 3, AssetHealthGrade.Critical, start.AddSeconds(20));

        var first = engine.Analyze(station, new[] { station, motor }, start.AddSeconds(30));
        var second = engine.Analyze(station, new[] { motor, station }, start.AddMinutes(1));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.AnalysisId, second!.AnalysisId);
        Assert.Equal(engine.GraphRegistration.GraphHash, first.GraphHash);
    }

    [Fact]
    public void Correlation_window_excludes_stale_events()
    {
        var options = Options();
        options.CorrelationWindowSeconds = 30;
        var engine = new AssetHealthRootCauseAnalysisEngine(options);
        var start = DateTime.UnixEpoch;
        var staleMotor = Event("E-STALE", "MOTOR-01", 1, AssetHealthGrade.Critical, start);
        var station = Event("E-STATION", "ST-01", 1, AssetHealthGrade.Critical, start.AddMinutes(10));

        var result = engine.Analyze(station, new[] { staleMotor, station }, start.AddMinutes(10));

        Assert.NotNull(result);
        Assert.Equal(1, result!.ObservedEventCount);
        Assert.DoesNotContain("E-STALE", result.ObservedEventIds);
    }

    [Fact]
    public void Maximum_depth_bounds_propagation_search()
    {
        var options = Options();
        options.MaximumPropagationDepth = 1;
        var engine = new AssetHealthRootCauseAnalysisEngine(options);
        var start = DateTime.UnixEpoch;
        var motor = Event("E-MOTOR", "MOTOR-01", 1, AssetHealthGrade.Degraded, start);
        var conveyor = Event("E-CONVEYOR", "CV-01", 1, AssetHealthGrade.Degraded, start.AddSeconds(10));
        var station = Event("E-STATION", "ST-01", 1, AssetHealthGrade.Critical, start.AddSeconds(20));

        var result = engine.Analyze(station, new[] { motor, conveyor, station }, start.AddSeconds(30));

        Assert.NotNull(result);
        var motorCandidate = Assert.Single(result!.Candidates.Where(candidate => candidate.NodeId == "N-MOTOR"));
        Assert.Equal(2, motorCandidate.SupportingEventCount);
        Assert.DoesNotContain(motorCandidate.PropagationPaths, path => path.TargetEventId == station.EventId);
    }

    [Fact]
    public void Cyclic_graph_is_rejected_when_cycles_are_disabled()
    {
        var options = Options();
        options.Graph.Edges.Add(new RootCauseGraphEdge
        {
            EdgeId = "E-BACK",
            UpstreamNodeId = "N-STATION",
            DownstreamNodeId = "N-MOTOR",
            RelationType = RootCauseRelationType.DependsOn,
            Weight = 1
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => new AssetHealthRootCauseAnalysisEngine(options));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Event_without_graph_mapping_is_not_analyzed()
    {
        var engine = new AssetHealthRootCauseAnalysisEngine(Options());
        var trigger = Event("E-UNKNOWN", "UNKNOWN", 1, AssetHealthGrade.Critical, DateTime.UnixEpoch);

        Assert.Null(engine.Analyze(trigger, new[] { trigger }, DateTime.UnixEpoch));
    }

    private static AssetHealthRootCauseOptions Options() => new()
    {
        Enabled = true,
        EvaluationIntervalSeconds = 10,
        CorrelationWindowSeconds = 100,
        MaximumPropagationDepth = 6,
        MaximumGraphNodes = 100,
        MaximumGraphEdges = 100,
        MaximumEventsPerAnalysis = 100,
        MaximumCandidates = 10,
        MaximumPaths = 100,
        MinimumCandidateConfidence = 0,
        Graph = new RootCauseGraphDefinition
        {
            Version = "graph-v1",
            Source = "unit-test",
            ApprovedBy = "tester",
            ApprovedAtUtc = DateTime.UnixEpoch,
            Nodes =
            {
                new RootCauseGraphNode
                {
                    NodeId = "N-MOTOR",
                    EntityId = "MOTOR-01",
                    DisplayName = "Motor 01",
                    Kind = RootCauseNodeKind.Component
                },
                new RootCauseGraphNode
                {
                    NodeId = "N-CONVEYOR",
                    EntityId = "CV-01",
                    DisplayName = "Conveyor 01",
                    Kind = RootCauseNodeKind.Asset
                },
                new RootCauseGraphNode
                {
                    NodeId = "N-STATION",
                    EntityId = "ST-01",
                    DisplayName = "Station 01",
                    Kind = RootCauseNodeKind.Station
                }
            },
            Edges =
            {
                new RootCauseGraphEdge
                {
                    EdgeId = "E-MOTOR-CV",
                    UpstreamNodeId = "N-MOTOR",
                    DownstreamNodeId = "N-CONVEYOR",
                    RelationType = RootCauseRelationType.Controls,
                    Weight = 0.9
                },
                new RootCauseGraphEdge
                {
                    EdgeId = "E-CV-ST",
                    UpstreamNodeId = "N-CONVEYOR",
                    DownstreamNodeId = "N-STATION",
                    RelationType = RootCauseRelationType.Feeds,
                    Weight = 0.8
                }
            }
        }
    };

    private static AssetHealthEventSnapshot Event(
        string eventId,
        string assetId,
        int version,
        AssetHealthGrade grade,
        DateTime firstDetectedUtc) => new()
    {
        EventId = eventId,
        EventKey = assetId,
        AssetId = assetId,
        Version = version,
        LifecycleStatus = AssetHealthEventLifecycleStatus.Active,
        Grade = grade,
        PeakGrade = grade,
        HealthScore = grade == AssetHealthGrade.Critical ? 20 : 50,
        LowestHealthScore = grade == AssetHealthGrade.Critical ? 20 : 50,
        FirstDetectedUtc = firstDetectedUtc,
        LastObservedUtc = firstDetectedUtc.AddSeconds(5),
        Acknowledged = false,
        IsSuppressed = false,
        Reason = $"{assetId} anomaly",
        Source = "unit-test",
        Category = "deterministic"
    };
}
