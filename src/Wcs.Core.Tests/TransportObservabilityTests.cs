using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportObservabilityTests
{
    [Fact]
    public void Telemetry_RecordsTraceAndAggregatedMetrics()
    {
        using var telemetry = new TransportTelemetryService();
        using (var operation = telemetry.StartOperation(
                   TransportTraceOperationKind.Dispatch,
                   "test.dispatch",
                   "TASK-01",
                   "EMS-01"))
        {
            operation.Complete(true, "ok");
        }

        var trace = Assert.Single(telemetry.GetRecentTraces());
        var metric = Assert.Single(telemetry.GetMetricsSnapshot().Operations);
        Assert.Equal("TASK-01", trace.RequestId);
        Assert.Equal("EMS-01", trace.VehicleId);
        Assert.True(trace.Success);
        Assert.Equal(TransportTraceOperationKind.Dispatch, metric.Kind);
        Assert.Equal(1, metric.TotalCount);
        Assert.Equal(0, metric.FailureCount);
    }

    [Fact]
    public async Task ConsistencyInspection_DetectsPositionMismatchWithoutMutatingRuntime()
    {
        using var telemetry = new TransportTelemetryService();
        var stateStore = new InMemoryTransportStateStore();
        await stateStore.SaveVehicleAsync(Vehicle("EMS-01", "N1"));
        var vehicles = new InMemoryTransportVehicleRegistry();
        vehicles.Upsert(Vehicle("EMS-01", "N2"));
        var alarms = new AlarmCenter(new EventBus());
        var service = new TransportConsistencyInspectionService(
            stateStore,
            vehicles,
            new EmptyExecutionEngine(),
            new EmptyReservationManager(),
            new TransportDriverDiagnosticsService(),
            telemetry,
            new InMemoryTransportJournalStore(),
            alarms,
            new TransportObservabilityOptions { RaiseConsistencyAlarms = false });

        var report = await service.InspectAsync();

        Assert.Contains(report.Issues, x =>
            x.IssueType == TransportConsistencyIssueType.VehiclePositionMismatch);
        Assert.True(vehicles.TryGet("EMS-01", out var runtime));
        Assert.Equal("N2", runtime!.CurrentNodeId);
        Assert.Equal(0, alarms.GetActiveCount());
    }

    [Fact]
    public async Task ConsistencyInspection_ReportsConsistentState()
    {
        using var telemetry = new TransportTelemetryService();
        var vehicle = Vehicle("EMS-01", "N1");
        var stateStore = new InMemoryTransportStateStore();
        await stateStore.SaveVehicleAsync(vehicle);
        var vehicles = new InMemoryTransportVehicleRegistry();
        vehicles.Upsert(vehicle);
        var service = new TransportConsistencyInspectionService(
            stateStore,
            vehicles,
            new EmptyExecutionEngine(),
            new EmptyReservationManager(),
            new TransportDriverDiagnosticsService(),
            telemetry,
            new InMemoryTransportJournalStore(),
            new AlarmCenter(new EventBus()),
            new TransportObservabilityOptions { RaiseConsistencyAlarms = false });

        var report = await service.InspectAsync();

        Assert.True(report.IsConsistent);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task HealthEvaluation_IgnoresObservabilitySelfAlarms()
    {
        using var telemetry = new TransportTelemetryService();
        var alarms = new AlarmCenter(new EventBus());
        alarms.SetAlarmRule(new AlarmRule
        {
            AlarmCode = "TRANSPORT_HEALTH",
            DelayRaiseMs = 1,
            DelayRecoverMs = 1,
            Level = AlarmLevelEnum.Warning
        });
        await alarms.RaiseAlarmAsync("TRANSPORT_HEALTH", AlarmLevelEnum.Warning, "self");
        await WaitUntilAsync(() => alarms.GetActiveCount() == 1);

        var service = new TransportObservabilityService(
            telemetry,
            new StaticConsistencyService(new TransportConsistencyReport()),
            new InMemoryTransportVehicleRegistry(),
            new EmptyExecutionEngine(),
            new EmptyReservationManager(),
            new EmptyProductionDispatchService(),
            new TransportDriverDiagnosticsService(),
            alarms,
            new InMemoryTransportJournalStore(),
            new TransportObservabilityOptions());

        var health = await service.EvaluateHealthAsync();

        var alarmComponent = Assert.Single(health.Components.Where(x => x.Component == "Alarm"));
        Assert.Equal(100, alarmComponent.Score);
    }

    [Fact]
    public async Task ConfigurationSnapshot_CapturesAllConfigurationFamilies()
    {
        using var telemetry = new TransportTelemetryService();
        var runtime = new TransportRuntimeConfiguration { Version = 3 };
        var tuning = new TransportProductionTuningOptions { Version = 4 };
        var service = new TransportConfigurationSnapshotService(
            new FakeConfigurationService(runtime),
            new FakeTuningService(tuning),
            new FakeStationService(new TransportStationDefinition
            {
                StationId = "ST-01",
                Name = "工位1",
                Capacity = 2
            }),
            new FakeSingleTrackService(new TransportSingleTrackSectionDefinition
            {
                SectionId = "TRACK-01",
                OrderedNodeIds = new[] { "N1", "N2" }
            }),
            new InMemoryTransportJournalStore(),
            telemetry);

        var snapshot = await service.CreateAsync("baseline", "上线前基线", "tester");

        Assert.Equal(3, snapshot.RuntimeConfiguration.Version);
        Assert.Equal(4, snapshot.ProductionTuning.Version);
        Assert.Single(snapshot.ProductionStations);
        Assert.Single(snapshot.SingleTrackSections);
        Assert.Single(await service.GetAsync());
    }

    [Fact]
    public async Task ConfigurationRollback_RejectsVersionChangeBeforeSafetySnapshot()
    {
        using var telemetry = new TransportTelemetryService();
        var configuration = new FakeConfigurationService(new TransportRuntimeConfiguration { Version = 2 });
        var tuning = new FakeTuningService(new TransportProductionTuningOptions { Version = 3 });
        var journal = new InMemoryTransportJournalStore();
        var service = new TransportConfigurationSnapshotService(
            configuration,
            tuning,
            new FakeStationService(),
            new FakeSingleTrackService(),
            journal,
            telemetry);
        var target = await service.CreateAsync("target", "target", "tester");

        var result = await service.RollbackAsync(
            target.SnapshotId,
            expectedRuntimeVersion: 1,
            expectedTuningVersion: 3,
            updatedBy: "operator");

        Assert.False(result.Success);
        Assert.Contains("运行时配置版本已变化", result.Error);
        Assert.Null(result.SafetySnapshotId);
    }

    [Fact]
    public async Task ConfigurationRollback_AppliesTargetAndCreatesSafetySnapshot()
    {
        using var telemetry = new TransportTelemetryService();
        var configuration = new FakeConfigurationService(new TransportRuntimeConfiguration { Version = 1 });
        var tuning = new FakeTuningService(new TransportProductionTuningOptions { Version = 1 });
        var stations = new FakeStationService(new TransportStationDefinition { StationId = "ST-OLD", Capacity = 1 });
        var tracks = new FakeSingleTrackService();
        var service = new TransportConfigurationSnapshotService(
            configuration,
            tuning,
            stations,
            tracks,
            new InMemoryTransportJournalStore(),
            telemetry);
        var target = await service.CreateAsync("target", "target", "tester");

        configuration.Current = new TransportRuntimeConfiguration { Version = 2 };
        tuning.CurrentValue = new TransportProductionTuningOptions { Version = 2, MaximumDispatchPerCycle = 8 };
        var result = await service.RollbackAsync(target.SnapshotId, 2, 2, "operator");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.SafetySnapshotId);
        Assert.Equal(3, configuration.Current.Version);
        Assert.Equal(3, tuning.Current.Version);
        Assert.Equal(target.ProductionTuning.MaximumDispatchPerCycle, tuning.Current.MaximumDispatchPerCycle);
    }

    private static TransportVehicleSnapshot Vehicle(string id, string node) => new()
    {
        VehicleId = id,
        Kind = TransportVehicleKind.Ems,
        State = TransportVehicleOperatingState.Idle,
        CurrentNodeId = node,
        IsOnline = true,
        BatteryPercent = 80,
        Version = 1
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
                throw new TimeoutException("等待测试条件超时");
            await Task.Delay(10);
        }
    }

    private sealed class EmptyExecutionEngine : ITransportExecutionEngine
    {
        public TransportExecutionResult Create(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Start(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult ApplyPositionFeedback(TransportPositionFeedback feedback) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmLoaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult ConfirmUnloaded(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Pause(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Resume(string requestId) => throw new NotSupportedException();
        public TransportExecutionResult Fault(string requestId, string reason) => throw new NotSupportedException();
        public TransportExecutionResult Cancel(string requestId, string? reason = null) => throw new NotSupportedException();
        public bool TryGet(string requestId, out TransportExecutionSnapshot? snapshot) { snapshot = null; return false; }
        public IReadOnlyList<TransportExecutionSnapshot> GetAll() => Array.Empty<TransportExecutionSnapshot>();
        public IReadOnlyList<TransportExecutionCommand> DequeueCommands(string vehicleId, int maxCount = 20) => Array.Empty<TransportExecutionCommand>();
    }

    private sealed class EmptyReservationManager : IRouteReservationManager
    {
        public bool TryReserve(string ownerId, IReadOnlyCollection<string> edgeIds, TimeSpan lease, out RouteReservation? reservation) { reservation = null; return false; }
        public bool TryExtend(string reservationId, IReadOnlyCollection<string> edgeIds, TimeSpan lease, out RouteReservation? reservation) { reservation = null; return false; }
        public bool ReleaseEdges(string reservationId, IReadOnlyCollection<string> edgeIds) => false;
        public bool Renew(string reservationId, TimeSpan lease) => false;
        public bool TryGet(string reservationId, out RouteReservation? reservation) { reservation = null; return false; }
        public bool Release(string reservationId) => false;
        public int CleanupExpired(DateTime? nowUtc = null) => 0;
        public IReadOnlyList<RouteReservation> GetActiveReservations() => Array.Empty<RouteReservation>();
    }

    private sealed class EmptyProductionDispatchService : ITransportProductionDispatchService
    {
        public TransportProductionQueueItem Enqueue(TransportProductionDispatchRequest request) => throw new NotSupportedException();
        public bool Cancel(string requestId) => false;
        public bool Complete(string requestId) => false;
        public Task<TransportProductionDispatchCycleResult> DispatchCycleAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<TransportProductionQueueItem> GetQueue() => Array.Empty<TransportProductionQueueItem>();
        public TransportProductionDryRunReport DryRun(DateTime? nowUtc = null) => new();
        public IReadOnlyList<TransportDispatchDecisionFrame> GetDecisions(int maxCount = 500) => Array.Empty<TransportDispatchDecisionFrame>();
    }

    private sealed class StaticConsistencyService : ITransportConsistencyInspectionService
    {
        private readonly TransportConsistencyReport _report;
        public StaticConsistencyService(TransportConsistencyReport report) => _report = report;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TransportConsistencyReport> InspectAsync(CancellationToken cancellationToken = default) => Task.FromResult(_report);
        public TransportConsistencyReport? GetLastReport() => _report;
        public IReadOnlyList<TransportConsistencyReport> GetRecentReports(int maxCount = 100) => new[] { _report };
    }

    private sealed class FakeConfigurationService : ITransportConfigurationService
    {
        public FakeConfigurationService(TransportRuntimeConfiguration current) => Current = current;
        public TransportRuntimeConfiguration Current { get; set; }
        public Task<TransportRuntimeConfiguration> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<TransportConfigurationSaveResult> SaveAndApplyAsync(TransportRuntimeConfiguration configuration, long expectedVersion, string updatedBy, CancellationToken cancellationToken = default)
        {
            if (Current.Version != expectedVersion)
                return Task.FromResult(TransportConfigurationSaveResult.Conflict(Current));
            Current = configuration with { Version = expectedVersion + 1, UpdatedBy = updatedBy, UpdatedAtUtc = DateTime.UtcNow };
            return Task.FromResult(TransportConfigurationSaveResult.Saved(Current));
        }
        public void Apply(TransportRuntimeConfiguration configuration) => Current = configuration;
    }

    private sealed class FakeTuningService : ITransportProductionTuningService
    {
        public FakeTuningService(TransportProductionTuningOptions current) => CurrentValue = current;
        public TransportProductionTuningOptions CurrentValue { get; set; }
        public TransportProductionTuningOptions Current => CurrentValue;
        public Task<TransportProductionTuningOptions> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentValue);
        public Task<TransportProductionTuningSaveResult> SaveAsync(TransportProductionTuningOptions options, long expectedVersion, string updatedBy, CancellationToken cancellationToken = default)
        {
            if (CurrentValue.Version != expectedVersion)
                return Task.FromResult(TransportProductionTuningSaveResult.Conflict(CurrentValue));
            CurrentValue = options with { Version = expectedVersion + 1, UpdatedBy = updatedBy, UpdatedAtUtc = DateTime.UtcNow };
            return Task.FromResult(TransportProductionTuningSaveResult.Saved(CurrentValue));
        }
    }

    private sealed class FakeStationService : ITransportStationCongestionService
    {
        private readonly Dictionary<string, TransportStationDefinition> _items = new(StringComparer.Ordinal);
        public FakeStationService(params TransportStationDefinition[] definitions)
        {
            foreach (var definition in definitions)
                _items[definition.StationId] = definition;
        }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveDefinitionAsync(TransportStationDefinition definition, CancellationToken cancellationToken = default) { _items[definition.StationId] = definition; return Task.CompletedTask; }
        public Task<bool> RemoveDefinitionAsync(string stationId, CancellationToken cancellationToken = default) => Task.FromResult(_items.Remove(stationId));
        public void UpdateOccupancy(string stationId, int occupiedCount) { }
        public void SetQueuedTaskCount(string stationId, int queuedTaskCount) { }
        public TransportStationAdmissionResult Evaluate(string? stationId) => new() { Allowed = true };
        public IReadOnlyList<TransportStationRuntimeSnapshot> GetAll() => _items.Values.Select(x => new TransportStationRuntimeSnapshot
        {
            StationId = x.StationId,
            Name = x.Name,
            Capacity = x.Capacity,
            MaximumQueuedTasks = x.MaximumQueuedTasks,
            Enabled = x.Enabled
        }).ToArray();
    }

    private sealed class FakeSingleTrackService : ITransportSingleTrackCoordinator
    {
        private readonly Dictionary<string, TransportSingleTrackSectionDefinition> _items = new(StringComparer.Ordinal);
        public FakeSingleTrackService(params TransportSingleTrackSectionDefinition[] definitions)
        {
            foreach (var definition in definitions)
                _items[definition.SectionId] = definition;
        }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveDefinitionAsync(TransportSingleTrackSectionDefinition definition, CancellationToken cancellationToken = default) { _items[definition.SectionId] = definition; return Task.CompletedTask; }
        public TransportSingleTrackAdmissionResult Evaluate(string ownerId, string vehicleId, int priority, IReadOnlyList<string> nodePath, DateTime? nowUtc = null) => TransportSingleTrackAdmissionResult.NotRequired();
        public void Commit(string ownerId, string vehicleId) { }
        public bool Release(string ownerId, bool requirePhysicalClearance = true) => true;
        public void CancelRequest(string ownerId) { }
        public IReadOnlyList<TransportSingleTrackSectionSnapshot> GetSnapshots() => _items.Values.Select(x => new TransportSingleTrackSectionSnapshot { Definition = x }).ToArray();
    }
}
