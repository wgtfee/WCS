using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportProductionDurabilityTests
{
    [Fact]
    public async Task ReliableProductionDispatch_CountsOneAttemptPerCycle()
    {
        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var stations = new TransportStationCongestionService(journal, tuning);
        var dispatch = new CompletingDispatchEngine();
        var service = new ReliableTransportProductionDispatchService(
            dispatch,
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
}
