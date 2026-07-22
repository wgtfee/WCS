namespace Wcs.Core.TransportScheduling;

public enum TransportSimulationSource
{
    Manual = 0,
    CurrentSnapshot = 1,
    HistoricalReplay = 2,
    CapacityStress = 3
}

public enum TransportSimulationStrategyKind
{
    BaselineDynamicPriority = 0,
    AgingFirst = 1,
    DeadlineFirst = 2,
    CongestionAware = 3,
    BalancedBatch = 4
}

public enum TransportSimulationFaultType
{
    VehicleOffline = 0,
    HeartbeatTimeout = 1,
    StationBlocked = 2,
    TrafficResourceBlocked = 3,
    DriverLatency = 4,
    CommandFailure = 5
}

public enum TransportAcceptanceState
{
    Passed = 0,
    Conditional = 1,
    Failed = 2
}

public sealed record TransportSimulationTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string SourceNodeId { get; init; } = string.Empty;
    public string DestinationNodeId { get; init; } = string.Empty;
    public string? DestinationStationId { get; init; }
    public IReadOnlyList<string> ResourceIds { get; init; } = Array.Empty<string>();
    public TransportVehicleKind? RequiredVehicleKind { get; init; }
    public int Priority { get; init; }
    public int ProductionOrderPriority { get; init; }
    public bool IsRecoveryTask { get; init; }
    public int ArrivalOffsetSeconds { get; init; }
    public int? DeadlineOffsetSeconds { get; init; }
    public int EstimatedTravelSeconds { get; init; } = 30;
    public int ServiceSeconds { get; init; } = 10;
}

public sealed record TransportSimulationVehicle
{
    public string VehicleId { get; init; } = string.Empty;
    public TransportVehicleKind Kind { get; init; }
    public int InitialAvailableOffsetSeconds { get; init; }
    public int BatteryPercent { get; init; } = 100;
    public bool Online { get; init; } = true;
}

public sealed record TransportSimulationStation
{
    public string StationId { get; init; } = string.Empty;
    public int Capacity { get; init; } = 1;
    public int AdditionalServiceSeconds { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record TransportSimulationFault
{
    public string FaultId { get; init; } = Guid.NewGuid().ToString("N");
    public TransportSimulationFaultType FaultType { get; init; }
    public string TargetId { get; init; } = string.Empty;
    public int StartOffsetSeconds { get; init; }
    public int EndOffsetSeconds { get; init; }
    public int AddedLatencySeconds { get; init; }
    public double FailureProbability { get; init; }
}

public sealed record TransportSimulationScenario
{
    public string ScenarioId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TransportSimulationSource Source { get; init; }
    public DateTime BaseTimeUtc { get; init; } = DateTime.UtcNow;
    public DateTime? HistoricalFromUtc { get; init; }
    public DateTime? HistoricalToUtc { get; init; }
    public int HorizonSeconds { get; init; } = 3600;
    public int Seed { get; init; } = 20260722;
    public IReadOnlyList<TransportSimulationTask> Tasks { get; init; } = Array.Empty<TransportSimulationTask>();
    public IReadOnlyList<TransportSimulationVehicle> Vehicles { get; init; } = Array.Empty<TransportSimulationVehicle>();
    public IReadOnlyList<TransportSimulationStation> Stations { get; init; } = Array.Empty<TransportSimulationStation>();
    public IReadOnlyList<TransportSimulationFault> Faults { get; init; } = Array.Empty<TransportSimulationFault>();
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportSimulationPolicy
{
    public string PolicyId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public TransportSimulationStrategyKind Strategy { get; init; } = TransportSimulationStrategyKind.BaselineDynamicPriority;
    public int AgingPointsPerMinute { get; init; } = 2;
    public int DeadlineUrgencyPoints { get; init; } = 30;
    public int RecoveryTaskBoost { get; init; } = 50;
    public int CongestionPenaltyPerQueuedTask { get; init; } = 3;
    public int MaximumBatchSize { get; init; } = 5;
    public int MinimumBatteryPercent { get; init; } = 20;
    public bool FavorSameDestinationBatch { get; init; }
}

public sealed record TransportSimulationTaskResult
{
    public string TaskId { get; init; } = string.Empty;
    public string? VehicleId { get; init; }
    public bool Completed { get; init; }
    public bool DeadlineMissed { get; init; }
    public int ArrivalOffsetSeconds { get; init; }
    public int DispatchOffsetSeconds { get; init; }
    public int CompletionOffsetSeconds { get; init; }
    public int WaitingSeconds { get; init; }
    public int CycleSeconds { get; init; }
    public string? FailureReason { get; init; }
    public int EffectivePriority { get; init; }
}

public sealed record TransportSimulationResourceMetric
{
    public string ResourceId { get; init; } = string.Empty;
    public string ResourceType { get; init; } = string.Empty;
    public double UtilizationPercent { get; init; }
    public int MaximumConcurrentCount { get; init; }
    public int WaitingTaskCount { get; init; }
}

public sealed record TransportCongestionForecastPoint
{
    public int OffsetSeconds { get; init; }
    public int QueueLength { get; init; }
    public int ActiveTaskCount { get; init; }
    public double FleetUtilizationPercent { get; init; }
    public string CongestionLevel { get; init; } = "Clear";
}

public sealed record TransportSimulationMetrics
{
    public int TotalTaskCount { get; init; }
    public int CompletedTaskCount { get; init; }
    public int FailedTaskCount { get; init; }
    public double ThroughputPerHour { get; init; }
    public double AverageWaitingSeconds { get; init; }
    public double P95WaitingSeconds { get; init; }
    public int MaximumWaitingSeconds { get; init; }
    public double AverageCycleSeconds { get; init; }
    public double DeadlineMissRatePercent { get; init; }
    public int MaximumQueueLength { get; init; }
    public double FleetUtilizationPercent { get; init; }
    public double MaximumStationUtilizationPercent { get; init; }
    public int BlockedByFaultCount { get; init; }
    public double ObjectiveScore { get; init; }
}

public sealed record TransportSimulationRun
{
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; init; } = string.Empty;
    public string ScenarioName { get; init; } = string.Empty;
    public string PolicyId { get; init; } = string.Empty;
    public string PolicyName { get; init; } = string.Empty;
    public string InitiatedBy { get; init; } = string.Empty;
    public int Seed { get; init; }
    public TransportSimulationMetrics Metrics { get; init; } = new();
    public IReadOnlyList<TransportSimulationTaskResult> Tasks { get; init; } = Array.Empty<TransportSimulationTaskResult>();
    public IReadOnlyList<TransportSimulationResourceMetric> Resources { get; init; } = Array.Empty<TransportSimulationResourceMetric>();
    public IReadOnlyList<TransportCongestionForecastPoint> CongestionForecast { get; init; } = Array.Empty<TransportCongestionForecastPoint>();
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportStrategyComparisonItem
{
    public string PolicyId { get; init; } = string.Empty;
    public string PolicyName { get; init; } = string.Empty;
    public TransportSimulationStrategyKind Strategy { get; init; }
    public string RunId { get; init; } = string.Empty;
    public TransportSimulationMetrics Metrics { get; init; } = new();
    public int Rank { get; init; }
}

public sealed record TransportStrategyComparisonReport
{
    public string ComparisonId { get; init; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; init; } = string.Empty;
    public string ScenarioName { get; init; } = string.Empty;
    public string InitiatedBy { get; init; } = string.Empty;
    public IReadOnlyList<TransportStrategyComparisonItem> Items { get; init; } = Array.Empty<TransportStrategyComparisonItem>();
    public string? RecommendedPolicyId { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportBatchOptimizationResult
{
    public string OptimizationId { get; init; } = Guid.NewGuid().ToString("N");
    public string ScenarioId { get; init; } = string.Empty;
    public TransportSimulationPolicy RecommendedPolicy { get; init; } = new();
    public IReadOnlyList<string> RecommendedTaskOrder { get; init; } = Array.Empty<string>();
    public double ObjectiveScore { get; init; }
    public string Explanation { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportCapacityBenchmarkRequest
{
    public string Name { get; init; } = string.Empty;
    public int DurationMinutes { get; init; } = 60;
    public IReadOnlyList<int> VehicleCounts { get; init; } = new[] { 1, 2, 3, 4 };
    public IReadOnlyList<int> TaskRatesPerHour { get; init; } = new[] { 30, 60, 90, 120 };
    public int Repetitions { get; init; } = 3;
    public TransportVehicleKind VehicleKind { get; init; } = TransportVehicleKind.Ems;
    public int Seed { get; init; } = 20260722;
    public TransportSimulationPolicy Policy { get; init; } = new() { Name = "baseline" };
}

public sealed record TransportCapacityBenchmarkPoint
{
    public int VehicleCount { get; init; }
    public int TaskRatePerHour { get; init; }
    public double AverageCompletedTasks { get; init; }
    public double AverageThroughputPerHour { get; init; }
    public double AverageP95WaitingSeconds { get; init; }
    public double AverageDeadlineMissRatePercent { get; init; }
    public double AverageFleetUtilizationPercent { get; init; }
    public bool Sustainable { get; init; }
}

public sealed record TransportCapacityBenchmarkReport
{
    public string BenchmarkId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string InitiatedBy { get; init; } = string.Empty;
    public IReadOnlyList<TransportCapacityBenchmarkPoint> Points { get; init; } = Array.Empty<TransportCapacityBenchmarkPoint>();
    public int MaximumSustainableTaskRatePerHour { get; init; }
    public int RecommendedVehicleCount { get; init; }
    public string Conclusion { get; init; } = string.Empty;
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportAcceptanceCriteria
{
    public double MinimumThroughputPerHour { get; init; } = 30;
    public double MaximumP95WaitingSeconds { get; init; } = 120;
    public double MaximumDeadlineMissRatePercent { get; init; } = 5;
    public double MaximumFailureRatePercent { get; init; } = 1;
    public double MaximumFleetUtilizationPercent { get; init; } = 90;
    public int MaximumQueueLength { get; init; } = 20;
}

public sealed record TransportAcceptanceCheck
{
    public string Name { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public double ActualValue { get; init; }
    public double RequiredValue { get; init; }
    public string Comparison { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed record TransportFinalAcceptanceReport
{
    public string ReportId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string InitiatedBy { get; init; } = string.Empty;
    public string SimulationRunId { get; init; } = string.Empty;
    public string? ComparisonId { get; init; }
    public string? BenchmarkId { get; init; }
    public TransportAcceptanceState State { get; init; }
    public IReadOnlyList<TransportAcceptanceCheck> Checks { get; init; } = Array.Empty<TransportAcceptanceCheck>();
    public IReadOnlyList<string> RequiredManualChecks { get; init; } = Array.Empty<string>();
    public string Conclusion { get; init; } = string.Empty;
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportHistoricalReplayRequest
{
    public string Name { get; init; } = string.Empty;
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public int MaximumTasks { get; init; } = 1000;
    public int DefaultTravelSeconds { get; init; } = 30;
    public int DefaultServiceSeconds { get; init; } = 10;
}

public sealed record TransportSimulationSummary
{
    public TransportSimulationRun? LatestRun { get; init; }
    public TransportStrategyComparisonReport? LatestComparison { get; init; }
    public TransportCapacityBenchmarkReport? LatestBenchmark { get; init; }
    public TransportFinalAcceptanceReport? LatestAcceptance { get; init; }
    public int RunCount { get; init; }
    public int ComparisonCount { get; init; }
    public int BenchmarkCount { get; init; }
    public int AcceptanceReportCount { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TransportSimulationOptions
{
    public int MaximumScenarioTasks { get; init; } = 10000;
    public int MaximumStoredRuns { get; init; } = 200;
    public int MaximumStoredComparisons { get; init; } = 100;
    public int ForecastBucketSeconds { get; init; } = 60;
    public int DefaultTravelSeconds { get; init; } = 30;
    public int DefaultServiceSeconds { get; init; } = 10;
    public double SustainableP95WaitingSeconds { get; init; } = 120;
    public double SustainableDeadlineMissRatePercent { get; init; } = 5;
    public int HistoricalJournalLimit { get; init; } = 20000;
}
