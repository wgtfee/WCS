namespace Wcs.Core.TransportScheduling;

using Wcs.Core.RouteCenter;

public sealed record TransportProductionTuningOptions
{
    public long Version { get; init; }
    public int AgingPointsPerMinute { get; init; } = 2;
    public int MaximumAgingPoints { get; init; } = 100;
    public int DeadlineUrgencyWindowSeconds { get; init; } = 600;
    public int DeadlineUrgencyPoints { get; init; } = 30;
    public int RecoveryTaskBoost { get; init; } = 50;
    public int CongestionPenaltyPerQueuedTask { get; init; } = 3;
    public int FullStationPenalty { get; init; } = 100;
    public int MaximumDispatchPerCycle { get; init; } = 5;
    public int SingleTrackOppositeDirectionAgingSeconds { get; init; } = 60;
    public int TrendRetentionPoints { get; init; } = 1440;
    public int TrendCaptureIntervalSeconds { get; init; } = 60;
    public int FaultTakeoverCooldownSeconds { get; init; } = 30;
    public string UpdatedBy { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportProductionTuningSaveResult
{
    public bool Success { get; init; }
    public bool VersionConflict { get; init; }
    public TransportProductionTuningOptions? Options { get; init; }
    public string? Error { get; init; }

    public static TransportProductionTuningSaveResult Saved(TransportProductionTuningOptions options) =>
        new() { Success = true, Options = options };

    public static TransportProductionTuningSaveResult Conflict(TransportProductionTuningOptions current) =>
        new() { VersionConflict = true, Options = current, Error = "参数版本已变化，请刷新后重试" };

    public static TransportProductionTuningSaveResult Failed(string error) =>
        new() { Error = error };
}

public sealed record TransportStationDefinition
{
    public string StationId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Capacity { get; init; } = 1;
    public int MaximumQueuedTasks { get; init; } = 20;
    public bool Enabled { get; init; } = true;
}

public sealed record TransportStationRuntimeSnapshot
{
    public string StationId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Capacity { get; init; }
    public int OccupiedCount { get; init; }
    public int QueuedTaskCount { get; init; }
    public int MaximumQueuedTasks { get; init; }
    public bool Enabled { get; init; }
    public double UtilizationPercent { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportStationAdmissionResult
{
    public bool Allowed { get; init; }
    public int CongestionPenalty { get; init; }
    public string? Reason { get; init; }
}

public enum TransportSingleTrackDirection
{
    None = 0,
    Forward = 1,
    Reverse = 2
}

public sealed record TransportSingleTrackSectionDefinition
{
    public string SectionId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> OrderedNodeIds { get; init; } = Array.Empty<string>();
    public string? TrafficResourceId { get; init; }
    public int Capacity { get; init; } = 1;
    public int MaximumSameDirectionConvoy { get; init; } = 1;
    public bool Enabled { get; init; } = true;
}

public sealed record TransportSingleTrackWaitingRequest
{
    public string OwnerId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public string SectionId { get; init; } = string.Empty;
    public TransportSingleTrackDirection Direction { get; init; }
    public int Priority { get; init; }
    public DateTime WaitingSinceUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportSingleTrackPermit
{
    public string OwnerId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public string SectionId { get; init; } = string.Empty;
    public TransportSingleTrackDirection Direction { get; init; }
    public DateTime GrantedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportSingleTrackAdmissionResult
{
    public bool Required { get; init; }
    public bool Allowed { get; init; }
    public string? SectionId { get; init; }
    public TransportSingleTrackDirection Direction { get; init; }
    public string? Reason { get; init; }

    public static TransportSingleTrackAdmissionResult NotRequired() =>
        new() { Allowed = true };
}

public sealed record TransportSingleTrackSectionSnapshot
{
    public TransportSingleTrackSectionDefinition Definition { get; init; } = new();
    public TransportSingleTrackDirection ActiveDirection { get; init; }
    public IReadOnlyList<TransportSingleTrackPermit> ActivePermits { get; init; } = Array.Empty<TransportSingleTrackPermit>();
    public IReadOnlyList<TransportSingleTrackWaitingRequest> WaitingRequests { get; init; } = Array.Empty<TransportSingleTrackWaitingRequest>();
}

public sealed record TransportDispatchAdmissionContext
{
    public TransportDispatchRequest Request { get; init; } = new();
    public TransportVehicleSnapshot Vehicle { get; init; } = new();
    public TransportRouteResult PickupRoute { get; init; } = TransportRouteResult.NotFound();
    public TransportRouteResult LoadedRoute { get; init; } = TransportRouteResult.NotFound();
    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportDispatchAdmissionResult
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }

    public static TransportDispatchAdmissionResult Granted() => new() { Allowed = true };
    public static TransportDispatchAdmissionResult Denied(string reason) => new() { Reason = reason };
}

public interface ITransportDispatchAdmissionPolicy
{
    TransportDispatchAdmissionResult Evaluate(TransportDispatchAdmissionContext context);
    void OnAssigned(TransportDispatchAssignment assignment);
    void OnCompleted(TransportDispatchAssignment assignment);
    void CancelRequest(string requestId);
}

public sealed record TransportProductionDispatchRequest
{
    public TransportDispatchRequest Request { get; init; } = new();
    public string? SourceStationId { get; init; }
    public string? DestinationStationId { get; init; }
    public int ProductionOrderPriority { get; init; }
    public bool IsRecoveryTask { get; init; }
    public DateTime? DeadlineAtUtc { get; init; }
    public DateTime EnqueuedAtUtc { get; init; } = DateTime.UtcNow;
}

public enum TransportProductionQueueState
{
    Queued = 0,
    Dispatching = 1,
    Assigned = 2,
    WaitingForStation = 3,
    WaitingForTraffic = 4,
    WaitingForVehicle = 5,
    Failed = 6,
    Cancelled = 7
}

public sealed record TransportProductionQueueItem
{
    public TransportProductionDispatchRequest ProductionRequest { get; init; } = new();
    public TransportProductionQueueState State { get; init; } = TransportProductionQueueState.Queued;
    public int EffectivePriority { get; init; }
    public int AttemptCount { get; init; }
    public string? LastReason { get; init; }
    public string? AssignedVehicleId { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportProductionDispatchCycleResult
{
    public int ConsideredCount { get; init; }
    public int AssignedCount { get; init; }
    public int WaitingCount { get; init; }
    public IReadOnlyList<TransportProductionQueueItem> Items { get; init; } = Array.Empty<TransportProductionQueueItem>();
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportDispatchDecisionFrame
{
    public string DecisionId { get; init; } = Guid.NewGuid().ToString("N");
    public string RequestId { get; init; } = string.Empty;
    public int EffectivePriority { get; init; }
    public TransportProductionQueueState ResultState { get; init; }
    public string? VehicleId { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string> CompetingRequestIds { get; init; } = Array.Empty<string>();
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportProductionDryRunItem
{
    public string RequestId { get; init; } = string.Empty;
    public int EffectivePriority { get; init; }
    public int Rank { get; init; }
    public bool StationAdmitted { get; init; }
    public string? Explanation { get; init; }
}

public sealed record TransportProductionDryRunReport
{
    public IReadOnlyList<TransportProductionDryRunItem> Items { get; init; } = Array.Empty<TransportProductionDryRunItem>();
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportProductionTrendPoint
{
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
    public int QueueLength { get; init; }
    public int WaitingForStationCount { get; init; }
    public int WaitingForTrafficCount { get; init; }
    public int FaultedVehicleCount { get; init; }
    public int SingleTrackWaitingCount { get; init; }
    public double MaximumStationUtilizationPercent { get; init; }
    public double FleetUtilizationPercent { get; init; }
    public double CompletionRatePercent { get; init; }
}

public sealed record TransportProductionTrendSummary
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public int PointCount { get; init; }
    public double AverageQueueLength { get; init; }
    public int MaximumQueueLength { get; init; }
    public double AverageFleetUtilizationPercent { get; init; }
    public double AverageCompletionRatePercent { get; init; }
    public double MaximumStationUtilizationPercent { get; init; }
    public IReadOnlyList<TransportProductionTrendPoint> Points { get; init; } = Array.Empty<TransportProductionTrendPoint>();
}

public enum TransportFaultTakeoverDecision
{
    Reassigned = 0,
    ManualRecoveryRequired = 1,
    WaitingForPhysicalClearance = 2,
    NoAlternativeVehicle = 3,
    Skipped = 4,
    Failed = 5
}

public sealed record TransportFaultTakeoverItem
{
    public string RequestId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public TransportFaultTakeoverDecision Decision { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? ReplacementVehicleId { get; init; }
    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportFaultTakeoverReport
{
    public IReadOnlyList<TransportFaultTakeoverItem> Items { get; init; } = Array.Empty<TransportFaultTakeoverItem>();
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}
