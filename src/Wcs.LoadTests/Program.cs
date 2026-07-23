using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.TransportScheduling;

var outputPath = args.Length > 0 ? args[0] : Path.Combine("TestResults", "transport-loadtest-results.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

var report = new LoadTestReport
{
    StartedAtUtc = DateTime.UtcNow,
    CommitSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
    Environment = new EnvironmentSnapshot
    {
        OsDescription = RuntimeInformation.OSDescription,
        FrameworkDescription = RuntimeInformation.FrameworkDescription,
        ProcessorCount = Environment.ProcessorCount,
        IsServerGc = System.Runtime.GCSettings.IsServerGC,
        MachineName = Environment.MachineName
    }
};

Console.WriteLine($"LOADTEST_START commit={report.CommitSha} cpu={report.Environment.ProcessorCount} serverGc={report.Environment.IsServerGc}");

var scaleCases = new (int Tasks, int Vehicles)[]
{
    (100, 5),
    (500, 10),
    (1000, 20),
    (2500, 30),
    (5000, 50)
};

foreach (var testCase in scaleCases)
{
    var result = await RunSingleScaleAsync(testCase.Tasks, testCase.Vehicles);
    report.SingleScale.Add(result);
    Console.WriteLine(
        $"SCALE tasks={result.TaskCount} vehicles={result.VehicleCount} elapsedMs={result.ElapsedMilliseconds:F2} " +
        $"tasksPerSecond={result.EngineTasksPerSecond:F2} allocatedMb={result.AllocatedMegabytes:F2} " +
        $"heapMb={result.ManagedHeapMegabytes:F2} workingSetMb={result.WorkingSetMegabytes:F2} " +
        $"completed={result.CompletedTaskCount} failed={result.FailedTaskCount} p95Wait={result.P95WaitingSeconds:F2}");
}

report.Concurrent = await RunConcurrentAsync(concurrency: 8, tasksPerScenario: 1000, vehiclesPerScenario: 20);
Console.WriteLine(
    $"CONCURRENT concurrency={report.Concurrent.Concurrency} totalTasks={report.Concurrent.TotalTasks} " +
    $"elapsedMs={report.Concurrent.ElapsedMilliseconds:F2} tasksPerSecond={report.Concurrent.EngineTasksPerSecond:F2} " +
    $"allocatedMb={report.Concurrent.AllocatedMegabytes:F2} failures={report.Concurrent.Exceptions.Count}");

report.StrategyComparison = await RunStrategyComparisonAsync(taskCount: 1000, vehicleCount: 20);
Console.WriteLine(
    $"STRATEGY_COMPARE tasks={report.StrategyComparison.TaskCount} policies={report.StrategyComparison.PolicyCount} " +
    $"elapsedMs={report.StrategyComparison.ElapsedMilliseconds:F2} recommended={report.StrategyComparison.RecommendedPolicyId}");

report.CapacityBenchmark = await RunCapacityBenchmarkAsync();
Console.WriteLine(
    $"CAPACITY points={report.CapacityBenchmark.PointCount} elapsedMs={report.CapacityBenchmark.ElapsedMilliseconds:F2} " +
    $"maxRate={report.CapacityBenchmark.MaximumSustainableTaskRatePerHour} recommendedVehicles={report.CapacityBenchmark.RecommendedVehicleCount}");

report.CompletedAtUtc = DateTime.UtcNow;
report.TotalElapsedMilliseconds = (report.CompletedAtUtc - report.StartedAtUtc).TotalMilliseconds;

await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

Console.WriteLine($"LOADTEST_COMPLETE elapsedMs={report.TotalElapsedMilliseconds:F2} output={Path.GetFullPath(outputPath)}");

static async Task<ScaleResult> RunSingleScaleAsync(int taskCount, int vehicleCount)
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    var policy = BaselinePolicy();

    await service.RunAsync(BuildScenario(25, Math.Max(1, Math.Min(vehicleCount, 5)), 1000 + taskCount), policy, "loadtest-warmup");
    ForceGc();

    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    var process = Process.GetCurrentProcess();
    process.Refresh();
    var stopwatch = Stopwatch.StartNew();
    var run = await service.RunAsync(BuildScenario(taskCount, vehicleCount, 2000 + taskCount), policy, "loadtest");
    stopwatch.Stop();
    process.Refresh();
    var allocatedAfter = GC.GetTotalAllocatedBytes(true);
    var heap = GC.GetGCMemoryInfo().HeapSizeBytes;

    return new ScaleResult
    {
        TaskCount = taskCount,
        VehicleCount = vehicleCount,
        ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        EngineTasksPerSecond = taskCount / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
        MillisecondsPerTask = stopwatch.Elapsed.TotalMilliseconds / taskCount,
        AllocatedMegabytes = BytesToMb(allocatedAfter - allocatedBefore),
        ManagedHeapMegabytes = BytesToMb(heap),
        WorkingSetMegabytes = BytesToMb(process.WorkingSet64),
        CompletedTaskCount = run.Metrics.CompletedTaskCount,
        FailedTaskCount = run.Metrics.FailedTaskCount,
        ThroughputPerHour = run.Metrics.ThroughputPerHour,
        AverageWaitingSeconds = run.Metrics.AverageWaitingSeconds,
        P95WaitingSeconds = run.Metrics.P95WaitingSeconds,
        MaximumQueueLength = run.Metrics.MaximumQueueLength,
        FleetUtilizationPercent = run.Metrics.FleetUtilizationPercent
    };
}

static async Task<ConcurrentResult> RunConcurrentAsync(int concurrency, int tasksPerScenario, int vehiclesPerScenario)
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    await service.RunAsync(BuildScenario(25, 5, 777), BaselinePolicy(), "loadtest-warmup");
    ForceGc();

    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    var stopwatch = Stopwatch.StartNew();
    var operations = Enumerable.Range(0, concurrency)
        .Select(async index =>
        {
            try
            {
                var run = await service.RunAsync(
                    BuildScenario(tasksPerScenario, vehiclesPerScenario, 10000 + index),
                    BaselinePolicy() with { PolicyId = $"CONCURRENT-{index}" },
                    "loadtest-concurrent");
                return new ConcurrentOperation
                {
                    Index = index,
                    CompletedTaskCount = run.Metrics.CompletedTaskCount,
                    FailedTaskCount = run.Metrics.FailedTaskCount
                };
            }
            catch (Exception ex)
            {
                return new ConcurrentOperation
                {
                    Index = index,
                    Exception = $"{ex.GetType().Name}: {ex.Message}"
                };
            }
        })
        .ToArray();

    var completed = await Task.WhenAll(operations);
    stopwatch.Stop();
    var allocatedAfter = GC.GetTotalAllocatedBytes(true);

    return new ConcurrentResult
    {
        Concurrency = concurrency,
        TasksPerScenario = tasksPerScenario,
        TotalTasks = concurrency * tasksPerScenario,
        ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        EngineTasksPerSecond = concurrency * tasksPerScenario / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
        AllocatedMegabytes = BytesToMb(allocatedAfter - allocatedBefore),
        CompletedTaskCount = completed.Sum(x => x.CompletedTaskCount),
        FailedTaskCount = completed.Sum(x => x.FailedTaskCount),
        Exceptions = completed.Where(x => x.Exception is not null).Select(x => x.Exception!).ToList()
    };
}

static async Task<StrategyComparisonResult> RunStrategyComparisonAsync(int taskCount, int vehicleCount)
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    var policies = Enum.GetValues<TransportSimulationStrategyKind>()
        .Select(strategy => new TransportSimulationPolicy
        {
            PolicyId = strategy.ToString(),
            Name = strategy.ToString(),
            Strategy = strategy,
            FavorSameDestinationBatch = strategy == TransportSimulationStrategyKind.BalancedBatch
        })
        .ToArray();

    ForceGc();
    var stopwatch = Stopwatch.StartNew();
    var comparison = await service.CompareAsync(BuildScenario(taskCount, vehicleCount, 33001), policies, "loadtest-compare");
    stopwatch.Stop();

    return new StrategyComparisonResult
    {
        TaskCount = taskCount,
        VehicleCount = vehicleCount,
        PolicyCount = policies.Length,
        ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        RecommendedPolicyId = comparison.RecommendedPolicyId,
        Ranking = comparison.Items
            .OrderBy(x => x.Rank)
            .Select(x => new PolicyResult
            {
                Rank = x.Rank,
                PolicyId = x.PolicyId,
                ObjectiveScore = x.Metrics.ObjectiveScore,
                ThroughputPerHour = x.Metrics.ThroughputPerHour,
                P95WaitingSeconds = x.Metrics.P95WaitingSeconds,
                FailedTaskCount = x.Metrics.FailedTaskCount
            })
            .ToList()
    };
}

static async Task<CapacityBenchmarkResult> RunCapacityBenchmarkAsync()
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    ForceGc();
    var stopwatch = Stopwatch.StartNew();
    var benchmark = await service.RunCapacityBenchmarkAsync(new TransportCapacityBenchmarkRequest
    {
        Name = "loadtest-capacity",
        DurationMinutes = 30,
        VehicleCounts = new[] { 5, 10, 20 },
        TaskRatesPerHour = new[] { 300, 600, 900 },
        Repetitions = 2,
        Seed = 44001,
        Policy = BaselinePolicy()
    }, "loadtest-capacity");
    stopwatch.Stop();

    return new CapacityBenchmarkResult
    {
        PointCount = benchmark.Points.Count,
        ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        MaximumSustainableTaskRatePerHour = benchmark.MaximumSustainableTaskRatePerHour,
        RecommendedVehicleCount = benchmark.RecommendedVehicleCount,
        Points = benchmark.Points.ToList()
    };
}

static TransportSimulationScenario BuildScenario(int taskCount, int vehicleCount, int seed)
{
    const int stationCount = 10;
    const int resourceCount = 40;
    var vehicles = Enumerable.Range(1, vehicleCount)
        .Select(index => new TransportSimulationVehicle
        {
            VehicleId = $"EMS-{index:000}",
            Kind = TransportVehicleKind.Ems,
            Online = true,
            BatteryPercent = 100 - index % 15,
            InitialAvailableOffsetSeconds = index % 5
        })
        .ToArray();
    var stations = Enumerable.Range(1, stationCount)
        .Select(index => new TransportSimulationStation
        {
            StationId = $"S-{index:00}",
            Capacity = 2 + index % 3,
            AdditionalServiceSeconds = index % 4
        })
        .ToArray();
    var tasks = Enumerable.Range(0, taskCount)
        .Select(index => new TransportSimulationTask
        {
            TaskId = $"T-{seed}-{index:00000}",
            SourceNodeId = $"N-{index % 100:000}",
            DestinationNodeId = $"N-{(index * 7 + 13) % 100:000}",
            DestinationStationId = stations[index % stationCount].StationId,
            ResourceIds = new[]
            {
                $"R-{index % resourceCount:00}",
                $"R-{(index * 3 + 5) % resourceCount:00}"
            },
            RequiredVehicleKind = TransportVehicleKind.Ems,
            Priority = index % 100,
            ProductionOrderPriority = (index * 11) % 30,
            IsRecoveryTask = index % 127 == 0,
            ArrivalOffsetSeconds = index % 3600,
            DeadlineOffsetSeconds = index % 9 == 0 ? index % 3600 + 300 : null,
            EstimatedTravelSeconds = 20 + index % 41,
            ServiceSeconds = 5 + index % 16
        })
        .ToArray();

    return new TransportSimulationScenario
    {
        ScenarioId = $"LOAD-{seed}-{taskCount}-{vehicleCount}",
        Name = $"load-{taskCount}-tasks-{vehicleCount}-vehicles",
        Source = TransportSimulationSource.Manual,
        HorizonSeconds = 8 * 60 * 60,
        Seed = seed,
        Vehicles = vehicles,
        Stations = stations,
        Tasks = tasks
    };
}

static TransportSimulationPolicy BaselinePolicy() => new()
{
    PolicyId = "LOAD-BASELINE",
    Name = "load-baseline",
    Strategy = TransportSimulationStrategyKind.BaselineDynamicPriority,
    AgingPointsPerMinute = 2,
    DeadlineUrgencyPoints = 30,
    RecoveryTaskBoost = 50,
    CongestionPenaltyPerQueuedTask = 3,
    MaximumBatchSize = 5,
    MinimumBatteryPercent = 20
};

static ServiceProvider CreateProvider()
{
    var services = new ServiceCollection();
    services.AddSingleton<IAlarmCenter>(new AlarmCenter(new EventBus()));
    services.AddUnifiedTransportScheduling();
    return services.BuildServiceProvider();
}

static void ForceGc()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

static double BytesToMb(long bytes) => bytes / 1024d / 1024d;

internal sealed class LoadTestReport
{
    public string CommitSha { get; set; } = string.Empty;
    public EnvironmentSnapshot Environment { get; set; } = new();
    public List<ScaleResult> SingleScale { get; } = new();
    public ConcurrentResult Concurrent { get; set; } = new();
    public StrategyComparisonResult StrategyComparison { get; set; } = new();
    public CapacityBenchmarkResult CapacityBenchmark { get; set; } = new();
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public double TotalElapsedMilliseconds { get; set; }
}

internal sealed class EnvironmentSnapshot
{
    public string OsDescription { get; set; } = string.Empty;
    public string FrameworkDescription { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public bool IsServerGc { get; set; }
    public string MachineName { get; set; } = string.Empty;
}

internal sealed class ScaleResult
{
    public int TaskCount { get; set; }
    public int VehicleCount { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public double EngineTasksPerSecond { get; set; }
    public double MillisecondsPerTask { get; set; }
    public double AllocatedMegabytes { get; set; }
    public double ManagedHeapMegabytes { get; set; }
    public double WorkingSetMegabytes { get; set; }
    public int CompletedTaskCount { get; set; }
    public int FailedTaskCount { get; set; }
    public double ThroughputPerHour { get; set; }
    public double AverageWaitingSeconds { get; set; }
    public double P95WaitingSeconds { get; set; }
    public int MaximumQueueLength { get; set; }
    public double FleetUtilizationPercent { get; set; }
}

internal sealed class ConcurrentOperation
{
    public int Index { get; set; }
    public int CompletedTaskCount { get; set; }
    public int FailedTaskCount { get; set; }
    public string? Exception { get; set; }
}

internal sealed class ConcurrentResult
{
    public int Concurrency { get; set; }
    public int TasksPerScenario { get; set; }
    public int TotalTasks { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public double EngineTasksPerSecond { get; set; }
    public double AllocatedMegabytes { get; set; }
    public int CompletedTaskCount { get; set; }
    public int FailedTaskCount { get; set; }
    public List<string> Exceptions { get; set; } = new();
}

internal sealed class StrategyComparisonResult
{
    public int TaskCount { get; set; }
    public int VehicleCount { get; set; }
    public int PolicyCount { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public string? RecommendedPolicyId { get; set; }
    public List<PolicyResult> Ranking { get; set; } = new();
}

internal sealed class PolicyResult
{
    public int Rank { get; set; }
    public string PolicyId { get; set; } = string.Empty;
    public double ObjectiveScore { get; set; }
    public double ThroughputPerHour { get; set; }
    public double P95WaitingSeconds { get; set; }
    public int FailedTaskCount { get; set; }
}

internal sealed class CapacityBenchmarkResult
{
    public int PointCount { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public int MaximumSustainableTaskRatePerHour { get; set; }
    public int RecommendedVehicleCount { get; set; }
    public List<TransportCapacityBenchmarkPoint> Points { get; set; } = new();
}
