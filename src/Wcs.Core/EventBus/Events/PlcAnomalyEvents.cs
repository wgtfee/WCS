namespace Wcs.Core.EventBus.Events;

using Wcs.Core.AnomalyDetection;

public sealed class PlcAnomalyDetectedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;
    public required PlcAnomalyRecord Anomaly { get; init; }
}

public sealed class PlcAnomalyRecoveredEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;
    public required PlcAnomalyRecord Anomaly { get; init; }
}
