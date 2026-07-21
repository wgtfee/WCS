namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public interface ITransportExecutionEngine
{
    TransportExecutionResult Create(string requestId);
    TransportExecutionResult Start(string requestId);
    TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback);
    TransportExecutionResult ConfirmLoaded(string requestId);
    TransportExecutionResult ConfirmUnloaded(string requestId);
    TransportExecutionResult Pause(string requestId);
    TransportExecutionResult Resume(string requestId);
    TransportExecutionResult Fault(string requestId, string reason);
    TransportExecutionResult Cancel(string requestId, string? reason = null);
    bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot);
    IReadOnlyList<TransportExecutionSnapshot> GetAll();
    IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20);
}

public sealed class InMemoryTransportExecutionEngine : ITransportExecutionEngine
{
    private readonly IUnifiedTransportDispatchEngine _dispatchEngine;
    private readonly ITransportVehicleRegistry _vehicleRegistry;
    private readonly IRouteReservationManager _reservationManager;
    private readonly ConcurrentDictionary<string, TransportExecutionSnapshot> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TransportExecutionCommand>> _commands = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InMemoryTransportExecutionEngine(IUnifiedTransportDispatchEngine dispatchEngine, ITransportVehicleRegistry vehicleRegistry, IRouteReservationManager reservationManager)
    {
        _dispatchEngine = dispatchEngine;
        _vehicleRegistry = vehicleRegistry;
        _reservationManager = reservationManager;
    }

    public TransportExecutionResult Create(string requestId) => throw new NotImplementedException();
    public TransportExecutionResult Start(string requestId) => throw new NotImplementedException();
    public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback) => throw new NotImplementedException();
    public TransportExecutionResult ConfirmLoaded(string requestId) => throw new NotImplementedException();
    public TransportExecutionResult ConfirmUnloaded(string requestId) => throw new NotImplementedException();
    public TransportExecutionResult Pause(string requestId) => throw new NotImplementedException();
    public TransportExecutionResult Resume(string requestId) => throw new NotImplementedException();
    public TransportExecutionResult Fault(string requestId, string reason) => throw new NotImplementedException();
    public TransportExecutionResult Cancel(string requestId, string? reason = null) => throw new NotImplementedException();
    public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot) => _executions.TryGetValue(requestId, out snapshot);
    public IReadOnlyList<TransportExecutionSnapshot> GetAll() => _executions.Values.ToList();
    public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20) => Array.Empty<TransportExecutionCommand>();
}
