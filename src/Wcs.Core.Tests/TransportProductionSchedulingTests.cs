using Wcs.Core.ObjectTracking.Topology;
using Wcs.Core.RouteCenter;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportProductionSchedulingTests
{
    [Fact]
    public async Task Tuning_UsesOptimisticVersionAndPersists()
    {
        var journal = new InMemoryTransportJournalStore();
        var service = new TransportProductionTuningService(journal);

        var saved = await service.SaveAsync(
            new TransportProductionTuningOptions { MaximumDispatchPerCycle = 2 },
            0,
            "tester");
        var conflict = await service.SaveAsync(
            new TransportProductionTuningOptions { MaximumDispatchPerCycle = 3 },
            0,
            "tester");
        var restored = new TransportProductionTuningService(journal);
        await restored.LoadAsync();

        Assert.True(saved.Success);
        Assert.Equal(1, saved.Options!.Version);
        Assert.True(conflict.VersionConflict);
        Assert.Equal(2, restored.Current.MaximumDispatchPerCycle);
    }

    [Fact]
    public async Task DynamicPriority_AddsAgingDeadlineRecoveryAndSubtractsCongestion()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        await tuning.SaveAsync(new TransportProductionTuningOptions
        {
            AgingPointsPerMinute = 5,
            MaximumAgingPoints = 100,
            DeadlineUrgencyWindowSeconds = 600,
            DeadlineUrgencyPoints = 20,
            RecoveryTaskBoost = 30,
            CongestionPenaltyPerQueuedTask = 2,
            FullStationPenalty = 10
        }, 0, "tester");
        var stations = new TransportStationCongestionService(journal, tuning);
        await stations.SaveDefinitionAsync(new TransportStationDefinition
        {
            StationId = "S1",
            Capacity = 10,
            MaximumQueuedTasks = 20
        });
        stations.UpdateOccupancy("S1", 5);
        stations.SetQueuedTaskCount("S1", 2);
        var service = new TransportDynamicPriorityService(tuning, stations);
        var now = DateTime.UtcNow;

        var score = service.Calculate(new TransportProductionDispatchRequest
        {
            Request = Request("P1", 10),
            DestinationStationId = "S1",
            ProductionOrderPriority = 7,
            IsRecoveryTask = true,
            DeadlineAtUtc = now.AddMinutes(2),
            EnqueuedAtUtc = now.AddMinutes(-3)
        }, now);

        Assert.Equal(73, score);
    }

    [Fact]
    public async Task StationCongestion_DeniesFullStation()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var stations = new TransportStationCongestionService(journal, tuning);
        await stations.SaveDefinitionAsync(new TransportStationDefinition
        {
            StationId = "PACK",
            Capacity = 2,
            MaximumQueuedTasks = 5
        });
        stations.UpdateOccupancy("PACK", 2);

        var result = stations.Evaluate("PACK");

        Assert.False(result.Allowed);
        Assert.Contains("已满", result.Reason);
    }

    [Fact]
    public async Task SingleTrack_BlocksOppositeDirectionAndHonorsAgedWaiter()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        await tuning.SaveAsync(new TransportProductionTuningOptions
        {
            SingleTrackOppositeDirectionAgingSeconds = 10
        }, 0, "tester");
        var traffic = new TransportTrafficCoordinator();
        var single = new TransportSingleTrackCoordinator(journal, tuning, traffic);
        await single.SaveDefinitionAsync(new TransportSingleTrackSectionDefinition
        {
            SectionId = "ST-1",
            OrderedNodeIds = new[] { "N1", "N2", "N3" },
            Capacity = 2,
            MaximumSameDirectionConvoy = 2
        });
        var t0 = DateTime.UtcNow;

        var forward = single.Evaluate("A", "V1", 10, new[] { "N1", "N2", "N3" }, t0);
        single.Commit("A", "V1");
        var reverse = single.Evaluate("B", "V2", 10, new[] { "N3", "N2", "N1" }, t0);
        var nextForward = single.Evaluate("C", "V3", 10, new[] { "N1", "N2", "N3" }, t0.AddSeconds(11));

        Assert.True(forward.Allowed);
        Assert.False(reverse.Allowed);
        Assert.False(nextForward.Allowed);
        Assert.Contains("优先级", nextForward.Reason);
    }

    [Fact]
    public async Task SingleTrack_DoesNotReleaseConfirmedPhysicalOccupancy()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var traffic = new TransportTrafficCoordinator();
        traffic.RegisterResource(new TransportTrafficResourceDefinition
        {
            ResourceId = "TR-ST-1",
            Kind = TransportTrafficResourceKind.SingleTrack,
            EdgeIds = new[] { "E1" }
        });
        traffic.RegisterRequest("TASK-1", "V1", 10);
        Assert.True(traffic.TryAcquire("TASK-1", new[] { "E1" }, TimeSpan.FromMinutes(1)).Success);
        Assert.True(traffic.MarkOccupancy("TASK-1", "TR-ST-1", true));

        var single = new TransportSingleTrackCoordinator(journal, tuning, traffic);
        await single.SaveDefinitionAsync(new TransportSingleTrackSectionDefinition
        {
            SectionId = "ST-1",
            TrafficResourceId = "TR-ST-1",
            OrderedNodeIds = new[] { "N1", "N2" }
        });
        Assert.True(single.Evaluate("TASK-1", "V1", 10, new[] { "N1", "N2" }).Allowed);
        single.Commit("TASK-1", "V1");

        Assert.False(single.Release("TASK-1", requirePhysicalClearance: true));
        Assert.Single(single.GetSnapshots().Single().ActivePermits);
    }

    [Fact]
    public async Task DispatchEngine_AppliesSingleTrackAdmissionBeforeReservation()
    {
        var routeCenter = BidirectionalRouteCenter();
        var registry = new InMemoryTransportVehicleRegistry();
        registry.Upsert(Vehicle("V1", "N1"));
        registry.Upsert(Vehicle("V2", "N3"));
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var traffic = new TransportTrafficCoordinator();
        var single = new TransportSingleTrackCoordinator(journal, tuning, traffic);
        await single.SaveDefinitionAsync(new TransportSingleTrackSectionDefinition
        {
            SectionId = "ST",
            OrderedNodeIds = new[] { "N1", "N2", "N3" }
        });
        var engine = new UnifiedTransportDispatchEngine(
            registry,
            new DefaultTransportVehicleSelector(routeCenter),
            routeCenter,
            new InMemoryRouteReservationManager(routeCenter),
            traffic,
            new[] { new TransportSingleTrackDispatchAdmissionPolicy(single) });

        var first = await engine.DispatchAsync(Request("A", 10) with
        {
            SourceNodeId = "N1",
            DestinationNodeId = "N3",
            RequiredVehicleId = "V1"
        });
        var second = await engine.DispatchAsync(Request("B", 10) with
        {
            SourceNodeId = "N3",
            DestinationNodeId = "N1",
            RequiredVehicleId = "V2"
        });

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("单轨", second.FailureReason);
    }

    [Fact]
    public async Task ProductionQueue_DispatchesHighestEffectivePriorityFirst()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        await tuning.SaveAsync(new TransportProductionTuningOptions
        {
            MaximumDispatchPerCycle = 1
        }, 0, "tester");
        var stations = new TransportStationCongestionService(journal, tuning);
        var fakeDispatch = new FakeDispatchEngine();
        var service = new TransportProductionDispatchService(
            fakeDispatch,
            new TransportDynamicPriorityService(tuning, stations),
            stations,
            tuning,
            new InMemoryTransportDispatchDecisionStore());
        service.Enqueue(new TransportProductionDispatchRequest
        {
            Request = Request("LOW", 1),
            ProductionOrderPriority = 1
        });
        service.Enqueue(new TransportProductionDispatchRequest
        {
            Request = Request("HIGH", 1),
            ProductionOrderPriority = 50
        });

        var cycle = await service.DispatchCycleAsync();

        Assert.Equal(1, cycle.AssignedCount);
        Assert.Equal("HIGH", Assert.Single(fakeDispatch.DispatchOrder));
        Assert.Equal(TransportProductionQueueState.Assigned,
            service.GetQueue().Single(x => x.ProductionRequest.Request.RequestId == "HIGH").State);
    }

    [Fact]
    public async Task ProductionDryRun_DoesNotDispatchOrMutateQueue()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var stations = new TransportStationCongestionService(journal, tuning);
        var fakeDispatch = new FakeDispatchEngine();
        var service = new TransportProductionDispatchService(
            fakeDispatch,
            new TransportDynamicPriorityService(tuning, stations),
            stations,
            tuning,
            new InMemoryTransportDispatchDecisionStore());
        service.Enqueue(new TransportProductionDispatchRequest { Request = Request("DRY", 5) });

        var report = service.DryRun();

        Assert.Single(report.Items);
        Assert.Empty(fakeDispatch.DispatchOrder);
        Assert.Equal(TransportProductionQueueState.Queued, Assert.Single(service.GetQueue()).State);
    }

    [Fact]
    public async Task TrendCapture_ReportsQueueStationTrackAndFleetMetrics()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var stations = new TransportStationCongestionService(journal, tuning);
        await stations.SaveDefinitionAsync(new TransportStationDefinition { StationId = "S", Capacity = 2 });
        stations.UpdateOccupancy("S", 1);
        var traffic = new TransportTrafficCoordinator();
        var single = new TransportSingleTrackCoordinator(journal, tuning, traffic);
        await single.SaveDefinitionAsync(new TransportSingleTrackSectionDefinition
        {
            SectionId = "ST",
            OrderedNodeIds = new[] { "N1", "N2" }
        });
        single.Evaluate("WAIT", "V2", 1, new[] { "N2", "N1" });
        var production = new FakeProductionDispatchService(new[]
        {
            new TransportProductionQueueItem
            {
                ProductionRequest = new TransportProductionDispatchRequest { Request = Request("Q", 1) },
                State = TransportProductionQueueState.WaitingForTraffic
            }
        });
        var vehicles = new InMemoryTransportVehicleRegistry();
        vehicles.Upsert(Vehicle("V1", "N1") with { State = TransportVehicleOperatingState.Faulted });
        var trends = new TransportProductionTrendService(
            production,
            stations,
            single,
            vehicles,
            new FakePerformanceService(),
            tuning,
            journal);

        var point = await trends.CaptureAsync();
        var summary = trends.GetSummary(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(1, point.QueueLength);
        Assert.Equal(1, point.WaitingForTrafficCount);
        Assert.Equal(1, point.FaultedVehicleCount);
        Assert.Equal(50, point.MaximumStationUtilizationPercent);
        Assert.Single(summary.Points);
    }

    [Fact]
    public async Task FaultTakeover_UsesSafeReassignmentAndReportsReplacement()
    {
        var execution = new FakeExecutionEngine(new TransportExecutionSnapshot
        {
            RequestId = "TASK",
            VehicleId = "FAULTED",
            State = TransportExecutionState.MovingToPickup
        });
        var vehicles = new InMemoryTransportVehicleRegistry();
        vehicles.Upsert(Vehicle("FAULTED", "N1") with
        {
            IsOnline = false,
            State = TransportVehicleOperatingState.Faulted
        });
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var single = new TransportSingleTrackCoordinator(journal, tuning, new TransportTrafficCoordinator());
        var service = new TransportFaultTakeoverService(
            execution,
            vehicles,
            new FakeReassignmentService(),
            single,
            tuning);

        var report = await service.EvaluateAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(TransportFaultTakeoverDecision.Reassigned, item.Decision);
        Assert.Equal("SPARE", item.ReplacementVehicleId);
    }

    private static TransportDispatchRequest Request(string id, int priority) => new()
    {
        RequestId = id,
        SourceNodeId = "N1",
        DestinationNodeId = "N2",
        Priority = priority,
        RequiredCapability = TransportVehicleCapability.Carry,
        RouteStrategy = TransportRouteStrategy.Shortest
    };

    private static TransportVehicleSnapshot Vehicle(string id, string node) => new()
    {
        VehicleId = id,
        Kind = TransportVehicleKind.Ems,
        State = TransportVehicleOperatingState.Idle,
        CurrentNodeId = node,
        IsOnline = true,
        BatteryPercent = 80,
        Capabilities = TransportVehicleCapability.Carry,
        Version = 1
    };

    private static ITransportRouteCenter BidirectionalRouteCenter()
    {
        var graph = new TopologyGraph();
        graph.AddNode(new Node { NodeId = "N1" });
        graph.AddNode(new Node { NodeId = "N2" });
        graph.AddNode(new Node { NodeId = "N3" });
        graph.AddEdge(new Edge { EdgeId = "E12", FromNodeId = "N1", ToNodeId = "N2", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E23", FromNodeId = "N2", ToNodeId = "N3", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E32", FromNodeId = "N3", ToNodeId = "N2", Weight = 1 });
        graph.AddEdge(new Edge { EdgeId = "E21", FromNodeId = "N2", ToNodeId = "N1", Weight = 1 });
        return new TransportRouteCenter(graph);
    }

    private sealed class FakeDispatchEngine : IUnifiedTransportDispatchEngine
    {
        private readonly Dictionary<string, TransportDispatchAssignment> _assignments = new(StringComparer.Ordinal);
        public List<string> DispatchOrder { get; } = new();

        public Task<TransportDispatchResult> DispatchAsync(TransportDispatchRequest request, CancellationToken cancellationToken = default)
        {
            DispatchOrder.Add(request.RequestId);
            var assignment = new TransportDispatchAssignment
            {
                RequestId = request.RequestId,
                VehicleId = "V-" + request.RequestId,
                VehicleKind = TransportVehicleKind.Ems
            };
            _assignments[request.RequestId] = assignment;
            return Task.FromResult(TransportDispatchResult.Succeeded(assignment));
        }

        public bool TryGetAssignment(string requestId, out TransportDispatchAssignment? assignment) =>
            _assignments.TryGetValue(requestId, out assignment);

        public bool Complete(string requestId) => _assignments.Remove(requestId);
    }

    private sealed class FakeProductionDispatchService : ITransportProductionDispatchService
    {
        private readonly IReadOnlyList<TransportProductionQueueItem> _items;
        public FakeProductionDispatchService(IReadOnlyList<TransportProductionQueueItem> items) => _items = items;
        public TransportProductionQueueItem Enqueue(TransportProductionDispatchRequest request) => throw new NotSupportedException();
        public bool Cancel(string requestId) => false;
        public bool Complete(string requestId) => false;
        public Task<TransportProductionDispatchCycleResult> DispatchCycleAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<TransportProductionQueueItem> GetQueue() => _items;
        public TransportProductionDryRunReport DryRun(DateTime? nowUtc = null) => new();
        public IReadOnlyList<TransportDispatchDecisionFrame> GetDecisions(int maxCount = 500) => Array.Empty<TransportDispatchDecisionFrame>();
    }

    private sealed class FakePerformanceService : ITransportPerformanceService
    {
        public TransportPerformanceSnapshot GetSnapshot() => new()
        {
            FleetUtilizationPercent = 25,
            CompletionRatePercent = 75
        };
    }

    private sealed class FakeExecutionEngine : ITransportExecutionEngine
    {
        private readonly TransportExecutionSnapshot _snapshot;
        public FakeExecutionEngine(TransportExecutionSnapshot snapshot) => _snapshot = snapshot;
        public TransportExecutionResult Create(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Start(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmLoaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmUnloaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Pause(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Resume(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Fault(string requestId, string reason) => throw new NotSupportedException();
        public TransportExecutionResult Cancel(string requestId, string? reason = null) => throw new NotSupportedException();
        public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot)
        {
            snapshot = string.Equals(requestId, _snapshot.RequestId, StringComparison.Ordinal) ? _snapshot : null;
            return snapshot is not null;
        }
        public IReadOnlyList<TransportExecutionSnapshot> GetAll() => new[] { _snapshot };
        public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20) => Array.Empty<TransportExecutionCommand>();
    }

    private sealed class FakeReassignmentService : ITransportTaskReassignmentService
    {
        public Task<TransportTaskReassignmentResult> ReassignAsync(
            string requestId,
            string reason,
            bool startImmediately = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportTaskReassignmentResult
            {
                Success = true,
                Record = new TransportTaskReassignmentRecord
                {
                    OriginalRequestId = requestId,
                    OriginalVehicleId = "FAULTED",
                    ReplacementVehicleId = "SPARE",
                    Decision = TransportReassignmentDecision.Reassigned,
                    Reason = reason
                }
            });

        public IReadOnlyList<TransportTaskReassignmentRecord> GetHistory() => Array.Empty<TransportTaskReassignmentRecord>();
    }
}
