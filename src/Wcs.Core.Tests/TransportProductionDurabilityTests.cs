using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportProductionDurabilityTests
{
    [Fact]
    public async Task ReliableProductionDispatch_CountsOneAttemptAndStartsExecution()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var stations = new TransportStationCongestionService(journal, tuning);
        var dispatch = new CompletingDispatchEngine();
        var execution = new SuccessfulExecutionEngine();
        var service = new ReliableTransportProductionDispatchService(
            dispatch,
            execution,
            new TransportDynamicPriorityService(tuning, stations),
            stations,
            tuning,
            new InMemoryTransportDispatchDecisionStore());
        service.Enqueue(new TransportProductionDispatchRequest
        {
            Request = new TransportDispatchRequest
            {
                RequestId = "COUNT-1",
                SourceNodeId = "N1",
                DestinationNodeId = "N2",
                Priority = 10
            }
        });

        var result = await service.DispatchCycleAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(1, item.AttemptCount);
        Assert.Equal(TransportProductionQueueState.Assigned, item.State);
        Assert.Equal(1, Assert.Single(service.GetQueue()).AttemptCount);
        Assert.Equal(new[] { "COUNT-1" }, execution.StartedRequestIds);
    }

    [Fact]
    public async Task DecisionStore_RestoresPersistedFramesAfterRestart()
    {
        var journal = new InMemoryTransportJournalStore();
        var first = new JournalTransportDispatchDecisionStore(journal);
        await first.LoadAsync();
        first.Append(new TransportDispatchDecisionFrame
        {
            DecisionId = "DECISION-1",
            RequestId = "TASK-1",
            EffectivePriority = 88,
            ResultState = TransportProductionQueueState.WaitingForTraffic,
            Reason = "单轨区段反向占用"
        });

        var restored = new JournalTransportDispatchDecisionStore(journal);
        await restored.LoadAsync();

        var frame = Assert.Single(restored.GetRecent());
        Assert.Equal("DECISION-1", frame.DecisionId);
        Assert.Equal("TASK-1", frame.RequestId);
        Assert.Equal(88, frame.EffectivePriority);
        Assert.Equal(TransportProductionQueueState.WaitingForTraffic, frame.ResultState);
    }

    private sealed class CompletingDispatchEngine : IUnifiedTransportDispatchEngine
    {
        private readonly Dictionary<string, TransportDispatchAssignment> _assignments = new(StringComparer.Ordinal);

        public Task<TransportDispatchResult> DispatchAsync(
            TransportDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            var assignment = new TransportDispatchAssignment
            {
                RequestId = request.RequestId,
                VehicleId = "EMS-01",
                VehicleKind = TransportVehicleKind.Ems
            };
            _assignments[request.RequestId] = assignment;
            return Task.FromResult(TransportDispatchResult.Succeeded(assignment));
        }

        public bool TryGetAssignment(string requestId, out TransportDispatchAssignment? assignment) =>
            _assignments.TryGetValue(requestId, out assignment);

        public bool Complete(string requestId) => _assignments.Remove(requestId);
    }

    private sealed class SuccessfulExecutionEngine : ITransportExecutionEngine
    {
        private readonly Dictionary<string, TransportExecutionSnapshot> _snapshots = new(StringComparer.Ordinal);
        public List<string> StartedRequestIds { get; } = new();

        public TransportExecutionResult Create(string requestId)
        {
            if (!_snapshots.TryGetValue(requestId, out var snapshot))
            {
                snapshot = new TransportExecutionSnapshot
                {
                    RequestId = requestId,
                    VehicleId = "EMS-01",
                    State = TransportExecutionState.Assigned
                };
                _snapshots[requestId] = snapshot;
            }
            return TransportExecutionResult.Succeeded(snapshot);
        }

        public TransportExecutionResult Start(string requestId)
        {
            var created = Create(requestId);
            var started = created.Snapshot! with
            {
                State = TransportExecutionState.MovingToPickup,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _snapshots[requestId] = started;
            StartedRequestIds.Add(requestId);
            return TransportExecutionResult.Succeeded(started);
        }

        public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmLoaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmUnloaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Pause(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Resume(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Fault(string requestId, string reason) => throw new NotSupportedException();
        public TransportExecutionResult Cancel(string requestId, string? reason = null)
        {
            if (!_snapshots.TryGetValue(requestId, out var snapshot))
                return TransportExecutionResult.Failed("执行任务不存在");
            var cancelled = snapshot with
            {
                State = TransportExecutionState.Cancelled,
                LastError = reason
            };
            _snapshots[requestId] = cancelled;
            return TransportExecutionResult.Succeeded(cancelled);
        }
        public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot) =>
            _snapshots.TryGetValue(requestId, out snapshot);
        public bool TryGetActiveByVehicle(string vehicleId, out TransportExecutionSnapshot? snapshot)
        {
            var found = _snapshots.Values.FirstOrDefault(x =>
                !x.IsTerminal && string.Equals(x.VehicleId, vehicleId, StringComparison.Ordinal));
            snapshot = found;
            return found is not null;
        }
        public IReadOnlyList<TransportExecutionSnapshot> GetAll() => _snapshots.Values.ToArray();
        public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20) =>
            Array.Empty<TransportExecutionCommand>();
    }
}
