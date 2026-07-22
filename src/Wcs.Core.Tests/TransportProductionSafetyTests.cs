using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportProductionSafetyTests
{
    [Fact]
    public async Task FaultTakeover_DoesNotCreateReplacementBeforePhysicalClearance()
    {
        var execution = new SingleExecutionEngine(new TransportExecutionSnapshot
        {
            RequestId = "TASK-LOCKED",
            VehicleId = "EMS-FAULT",
            State = TransportExecutionState.MovingToPickup
        });
        var vehicles = new InMemoryTransportVehicleRegistry();
        vehicles.Upsert(new TransportVehicleSnapshot
        {
            VehicleId = "EMS-FAULT",
            Kind = TransportVehicleKind.Ems,
            State = TransportVehicleOperatingState.Faulted,
            CurrentNodeId = "N1",
            IsOnline = false,
            BatteryPercent = 50,
            Capabilities = TransportVehicleCapability.Carry,
            Version = 1
        });
        var traffic = new TransportTrafficCoordinator();
        traffic.RegisterResource(new TransportTrafficResourceDefinition
        {
            ResourceId = "TRACK-LOCK",
            Kind = TransportTrafficResourceKind.SingleTrack,
            EdgeIds = new[] { "E1" }
        });
        traffic.RegisterRequest("TASK-LOCKED", "EMS-FAULT", 10);
        Assert.True(traffic.TryAcquire(
            "TASK-LOCKED",
            new[] { "E1" },
            TimeSpan.FromMinutes(1)).Success);
        Assert.True(traffic.MarkOccupancy("TASK-LOCKED", "TRACK-LOCK", true));

        var journal = new InMemoryTransportJournalStore();
        var tuning = new TransportProductionTuningService(journal);
        var singleTrack = new TransportSingleTrackCoordinator(journal, tuning, traffic);
        var reassignments = new CountingReassignmentService();
        var service = new SafeTransportFaultTakeoverService(
            execution,
            vehicles,
            reassignments,
            singleTrack,
            traffic,
            tuning);

        var report = await service.EvaluateAsync();

        var item = Assert.Single(report.Items);
        Assert.Equal(TransportFaultTakeoverDecision.WaitingForPhysicalClearance, item.Decision);
        Assert.Equal(0, reassignments.CallCount);
        Assert.Contains("禁止创建接替车辆", item.Message);
    }

    private sealed class CountingReassignmentService : ITransportTaskReassignmentService
    {
        public int CallCount { get; private set; }

        public Task<TransportTaskReassignmentResult> ReassignAsync(
            string requestId,
            string reason,
            bool startImmediately = true,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new TransportTaskReassignmentResult
            {
                Success = true,
                Record = new TransportTaskReassignmentRecord
                {
                    OriginalRequestId = requestId,
                    ReplacementRequestId = requestId + ":replacement",
                    OriginalVehicleId = "EMS-FAULT",
                    ReplacementVehicleId = "EMS-SPARE",
                    Decision = TransportReassignmentDecision.Reassigned,
                    Reason = reason
                }
            });
        }

        public IReadOnlyList<TransportTaskReassignmentRecord> GetHistory() =>
            Array.Empty<TransportTaskReassignmentRecord>();
    }

    private sealed class SingleExecutionEngine : ITransportExecutionEngine
    {
        private readonly Dictionary<string, TransportExecutionSnapshot> _items;

        public SingleExecutionEngine(TransportExecutionSnapshot snapshot)
        {
            _items = new Dictionary<string, TransportExecutionSnapshot>(StringComparer.Ordinal)
            {
                [snapshot.RequestId] = snapshot
            };
        }

        public TransportExecutionResult Create(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Start(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmLoaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmUnloaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Pause(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Resume(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Fault(string requestId, string reason) => throw new NotSupportedException();
        public TransportExecutionResult Cancel(string requestId, string? reason = null)
        {
            if (!_items.TryGetValue(requestId, out var current))
                return TransportExecutionResult.Failed("执行任务不存在");
            var next = current with { State = TransportExecutionState.Cancelled, LastError = reason };
            _items[requestId] = next;
            return TransportExecutionResult.Succeeded(next);
        }
        public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot) =>
            _items.TryGetValue(requestId, out snapshot);
        public IReadOnlyList<TransportExecutionSnapshot> GetAll() => _items.Values.ToArray();
        public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20) =>
            Array.Empty<TransportExecutionCommand>();
    }
}
