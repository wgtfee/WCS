using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportSimulationTests
{
    [Fact]
    public async Task Simulation_SameScenarioPolicyAndSeed_IsDeterministic()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();
        var scenario = CreateScenario();
        var policy = new TransportSimulationPolicy
        {
            PolicyId = "POLICY-01",
            Name = "baseline",
            Strategy = TransportSimulationStrategyKind.BaselineDynamicPriority
        };

        var first = await service.RunAsync(scenario, policy, "tester");
        var second = await service.RunAsync(scenario, policy, "tester");

        Assert.Equal(first.Metrics, second.Metrics);
        Assert.True(first.Tasks.SequenceEqual(second.Tasks));
        Assert.True(first.CongestionForecast.SequenceEqual(second.CongestionForecast));
    }

    [Fact]
    public async Task Simulation_DoesNotMutateProductionVehicleOrStationState()
    {
        using var provider = CreateProvider();
        var vehicles = provider.GetRequiredService<ITransportVehicleRegistry>();
        var stations = provider.GetRequiredService<ITransportStationCongestionService>();
        vehicles.Upsert(new TransportVehicleSnapshot
        {
            VehicleId = "EMS-01",
            Kind = TransportVehicleKind.Ems,
            State = TransportVehicleOperatingState.Idle,
            CurrentNodeId = "N1",
            IsOnline = true,
            BatteryPercent = 90,
            Version = 1
        });
        await stations.SaveDefinitionAsync(new TransportStationDefinition
        {
            StationId = "S1",
            Name = "S1",
            Capacity = 1
        });
        var vehicleBefore = Assert.Single(vehicles.GetAll());
        var stationBefore = Assert.Single(stations.GetAll());
        var service = provider.GetRequiredService<ITransportSimulationService>();

        await service.RunAsync(CreateScenario(), new TransportSimulationPolicy { Name = "baseline" }, "tester");

        Assert.Equal(vehicleBefore, Assert.Single(vehicles.GetAll()));
        AssertStationUnchanged(stationBefore, Assert.Single(stations.GetAll()));
    }

    [Fact]
    public async Task StrategyComparison_DeadlineFirstBeatsBaselineWhenUrgentTaskWouldMiss()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();
        var scenario = new TransportSimulationScenario
        {
            Name = "deadline-ab",
            HorizonSeconds = 300,
            Seed = 7,
            Vehicles = new[] { Vehicle("EMS-01") },
            Tasks = new[]
            {
                new TransportSimulationTask
                {
                    TaskId = "NORMAL",
                    SourceNodeId = "N1",
                    DestinationNodeId = "N2",
                    Priority = 50,
                    EstimatedTravelSeconds = 100,
                    ServiceSeconds = 0
                },
                new TransportSimulationTask
                {
                    TaskId = "URGENT",
                    SourceNodeId = "N1",
                    DestinationNodeId = "N3",
                    DeadlineOffsetSeconds = 60,
                    EstimatedTravelSeconds = 20,
                    ServiceSeconds = 0
                }
            }
        };
        var baseline = new TransportSimulationPolicy
        {
            PolicyId = "BASE",
            Name = "baseline",
            Strategy = TransportSimulationStrategyKind.BaselineDynamicPriority,
            DeadlineUrgencyPoints = 30
        };
        var deadline = baseline with
        {
            PolicyId = "DEADLINE",
            Name = "deadline",
            Strategy = TransportSimulationStrategyKind.DeadlineFirst
        };

        var report = await service.CompareAsync(scenario, new[] { baseline, deadline }, "tester");

        Assert.Equal("DEADLINE", report.RecommendedPolicyId);
        Assert.Equal(1, report.Items.Single(x => x.PolicyId == "DEADLINE").Rank);
        Assert.True(report.Items.Single(x => x.PolicyId == "DEADLINE").Metrics.DeadlineMissRatePercent <
                    report.Items.Single(x => x.PolicyId == "BASE").Metrics.DeadlineMissRatePercent);
    }

    [Fact]
    public async Task FaultInjection_IsDeterministicAndDoesNotWriteProductionDriver()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();
        var scenario = CreateScenario() with
        {
            Vehicles = new[] { Vehicle("EMS-01") },
            Faults = new[]
            {
                new TransportSimulationFault
                {
                    FaultId = "F-OFFLINE",
                    FaultType = TransportSimulationFaultType.VehicleOffline,
                    TargetId = "EMS-01",
                    StartOffsetSeconds = 0,
                    EndOffsetSeconds = 100
                },
                new TransportSimulationFault
                {
                    FaultId = "F-CMD",
                    FaultType = TransportSimulationFaultType.CommandFailure,
                    TargetId = "EMS-01",
                    StartOffsetSeconds = 0,
                    EndOffsetSeconds = 300,
                    FailureProbability = 1
                }
            }
        };

        var run = await service.RunAsync(scenario, new TransportSimulationPolicy { Name = "fault-policy" }, "tester");

        Assert.All(run.Tasks, x => Assert.False(x.Completed));
        Assert.All(run.Tasks, x => Assert.True(x.DispatchOffsetSeconds >= 100));
        Assert.All(run.Tasks, x => Assert.Contains("故障", x.FailureReason ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoricalReplay_BuildsScenarioFromPersistedQueueRecords()
    {
        using var provider = CreateProvider();
        var journal = provider.GetRequiredService<ITransportJournalStore>();
        var now = DateTime.UtcNow;
        var item = new TransportProductionQueueItem
        {
            ProductionRequest = new TransportProductionDispatchRequest
            {
                Request = new TransportDispatchRequest
                {
                    RequestId = "HIST-01",
                    SourceNodeId = "N1",
                    DestinationNodeId = "N2",
                    Priority = 10
                },
                EnqueuedAtUtc = now.AddMinutes(-5)
            },
            UpdatedAtUtc = now.AddMinutes(-4)
        };
        await journal.UpsertAsync(new TransportJournalRecord
        {
            Category = TransportJournalCategory.ProductionQueue,
            RecordId = "HIST-01",
            PayloadJson = JsonSerializer.Serialize(item),
            OccurredAtUtc = now.AddMinutes(-4)
        });
        var vehicles = provider.GetRequiredService<ITransportVehicleRegistry>();
        vehicles.Upsert(new TransportVehicleSnapshot
        {
            VehicleId = "EMS-01",
            Kind = TransportVehicleKind.Ems,
            State = TransportVehicleOperatingState.Idle,
            CurrentNodeId = "N1",
            IsOnline = true,
            Version = 1
        });
        var service = provider.GetRequiredService<ITransportSimulationService>();

        var scenario = await service.BuildHistoricalScenarioAsync(new TransportHistoricalReplayRequest
        {
            Name = "history",
            FromUtc = now.AddHours(-1),
            ToUtc = now,
            MaximumTasks = 100
        });

        var task = Assert.Single(scenario.Tasks);
        Assert.Equal("HIST-01", task.TaskId);
        Assert.Equal(TransportSimulationSource.HistoricalReplay, scenario.Source);
        Assert.Single(scenario.Vehicles);
    }

    [Fact]
    public async Task CapacityBenchmark_MoreVehiclesDoNotReduceCompletedTasksAtSameRate()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();

        var report = await service.RunCapacityBenchmarkAsync(new TransportCapacityBenchmarkRequest
        {
            Name = "capacity",
            DurationMinutes = 30,
            VehicleCounts = new[] { 1, 2 },
            TaskRatesPerHour = new[] { 60 },
            Repetitions = 2,
            Policy = new TransportSimulationPolicy { Name = "baseline" },
            Seed = 17
        }, "tester");

        Assert.Equal(2, report.Points.Count);
        var one = report.Points.Single(x => x.VehicleCount == 1);
        var two = report.Points.Single(x => x.VehicleCount == 2);
        Assert.True(two.AverageCompletedTasks >= one.AverageCompletedTasks);
    }

    [Fact]
    public async Task CapacityBenchmark_DrainsTailWithoutTreatingOutstandingTasksAsFailures()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();

        var report = await service.RunCapacityBenchmarkAsync(new TransportCapacityBenchmarkRequest
        {
            Name = "capacity-drain",
            DurationMinutes = 5,
            VehicleCounts = new[] { 1, 20 },
            TaskRatesPerHour = new[] { 300 },
            Repetitions = 1,
            Policy = new TransportSimulationPolicy { Name = "baseline" },
            Seed = 20260723
        }, "tester");

        var overloaded = report.Points.Single(x => x.VehicleCount == 1);
        var sustainable = report.Points.Single(x => x.VehicleCount == 20);

        Assert.Equal(25d, sustainable.AverageArrivedTasks);
        Assert.True(sustainable.AverageCompletedTasks < sustainable.AverageArrivedTasks);
        Assert.True(sustainable.AverageOutstandingTasksAtCutoff > 0);
        Assert.Equal(0d, sustainable.AverageFailedTasks);
        Assert.Equal(
            sustainable.AverageArrivedTasks,
            sustainable.AverageCompletedTasks +
            sustainable.AverageOutstandingTasksAtCutoff +
            sustainable.AverageFailedTasks);
        Assert.True(sustainable.Sustainable);

        Assert.False(overloaded.Sustainable);
        Assert.True(overloaded.AverageP95WaitingSeconds > 120);
        Assert.Equal(300, report.MaximumSustainableTaskRatePerHour);
        Assert.Equal(20, report.RecommendedVehicleCount);
    }

    [Fact]
    public async Task AcceptanceReport_UsesExplicitThresholds()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();
        var run = await service.RunAsync(
            CreateScenario(),
            new TransportSimulationPolicy { Name = "baseline" },
            "tester");

        var passed = await service.GenerateAcceptanceReportAsync(
            "pass",
            run.RunId,
            new TransportAcceptanceCriteria
            {
                MinimumThroughputPerHour = 1,
                MaximumP95WaitingSeconds = 1000,
                MaximumDeadlineMissRatePercent = 100,
                MaximumFailureRatePercent = 100,
                MaximumFleetUtilizationPercent = 100,
                MaximumQueueLength = 100
            },
            "tester");
        var failed = await service.GenerateAcceptanceReportAsync(
            "fail",
            run.RunId,
            new TransportAcceptanceCriteria
            {
                MinimumThroughputPerHour = 10000,
                MaximumP95WaitingSeconds = 0,
                MaximumDeadlineMissRatePercent = 0,
                MaximumFailureRatePercent = 0,
                MaximumFleetUtilizationPercent = 1,
                MaximumQueueLength = 0
            },
            "tester");

        Assert.Equal(TransportAcceptanceState.Passed, passed.State);
        Assert.NotEqual(TransportAcceptanceState.Passed, failed.State);
        Assert.Contains(failed.Checks, x => !x.Passed);
        Assert.NotEmpty(failed.RequiredManualChecks);
    }

    [Fact]
    public async Task BatchOptimization_ReturnsRecommendationWithoutChangingProductionTuning()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();
        var tuning = provider.GetRequiredService<ITransportProductionTuningService>();
        var before = tuning.Current;

        var result = await service.OptimizeBatchAsync(CreateScenario(), "tester");

        Assert.NotEmpty(result.RecommendedTaskOrder);
        Assert.False(string.IsNullOrWhiteSpace(result.RecommendedPolicy.Name));
        Assert.Equal(before, tuning.Current);
    }

    [Fact]
    public async Task CongestionForecast_ReportsHeavyLevelForOverloadedFleet()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();
        var tasks = Enumerable.Range(0, 20).Select(index => new TransportSimulationTask
        {
            TaskId = $"Q-{index:00}",
            SourceNodeId = "N1",
            DestinationNodeId = "N2",
            ArrivalOffsetSeconds = 0,
            EstimatedTravelSeconds = 100,
            ServiceSeconds = 0
        }).ToArray();
        var scenario = new TransportSimulationScenario
        {
            Name = "overload",
            HorizonSeconds = 1000,
            Vehicles = new[] { Vehicle("EMS-01") },
            Tasks = tasks
        };

        var run = await service.RunAsync(scenario, new TransportSimulationPolicy { Name = "baseline" }, "tester");

        Assert.Contains(run.CongestionForecast, x => x.CongestionLevel == "Heavy");
        Assert.True(run.Metrics.MaximumQueueLength >= 10);
    }

    private static TransportSimulationScenario CreateScenario() => new()
    {
        ScenarioId = "SCENARIO-01",
        Name = "basic",
        HorizonSeconds = 600,
        Seed = 42,
        Vehicles = new[] { Vehicle("EMS-01"), Vehicle("EMS-02") },
        Stations = new[]
        {
            new TransportSimulationStation
            {
                StationId = "S1",
                Capacity = 1
            }
        },
        Tasks = new[]
        {
            new TransportSimulationTask
            {
                TaskId = "T1",
                SourceNodeId = "N1",
                DestinationNodeId = "N2",
                DestinationStationId = "S1",
                Priority = 10,
                ArrivalOffsetSeconds = 0,
                DeadlineOffsetSeconds = 200,
                EstimatedTravelSeconds = 30,
                ServiceSeconds = 10
            },
            new TransportSimulationTask
            {
                TaskId = "T2",
                SourceNodeId = "N2",
                DestinationNodeId = "N3",
                DestinationStationId = "S1",
                Priority = 5,
                ArrivalOffsetSeconds = 10,
                DeadlineOffsetSeconds = 300,
                EstimatedTravelSeconds = 40,
                ServiceSeconds = 10
            }
        }
    };

    private static TransportSimulationVehicle Vehicle(string vehicleId) => new()
    {
        VehicleId = vehicleId,
        Kind = TransportVehicleKind.Ems,
        Online = true,
        BatteryPercent = 100
    };

    private static void AssertStationUnchanged(
        TransportStationRuntimeSnapshot before,
        TransportStationRuntimeSnapshot after)
    {
        Assert.Equal(before.StationId, after.StationId);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Capacity, after.Capacity);
        Assert.Equal(before.OccupiedCount, after.OccupiedCount);
        Assert.Equal(before.QueuedTaskCount, after.QueuedTaskCount);
        Assert.Equal(before.MaximumQueuedTasks, after.MaximumQueuedTasks);
        Assert.Equal(before.Enabled, after.Enabled);
        Assert.Equal(before.UtilizationPercent, after.UtilizationPercent);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAlarmCenter>(new AlarmCenter(new EventBus()));
        services.AddUnifiedTransportScheduling();
        return services.BuildServiceProvider();
    }
}
