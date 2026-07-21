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
