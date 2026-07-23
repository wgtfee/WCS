using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.TransportScheduling;

var outputDirectory = Path.Combine(AppContext.BaseDirectory, "loadtest-results");
Directory.CreateDirectory(outputDirectory);

var report = new LoadTestReport
{
    StartedAtUtc = DateTime.UtcNow,
    MachineName = Environment.MachineName,
    OperatingSystem = RuntimeInformation.OSDescription,
    Framework = RuntimeInformation.FrameworkDescription,
    ProcessorCount = Environment.ProcessorCount,
    IsServerGc = System.Runtime.GCSettings.IsServerGC
};

Console.WriteLine($"Runtime: {report.Framework}");
Console.WriteLine($"OS: {report.OperatingSystem}");
Console.WriteLine($"CPU: {report.ProcessorCount}, ServerGC: {report.IsServerGc}");

foreach (var taskCount in new[] { 100, 500, 1_000, 2_500, 5_000 })
{
    var vehicleCount = Math.Clamp((int)Math.Ceiling(taskCount / 50d), 4, 100);
    report.SingleRunCases.Add(await RunSingleCaseAsync(taskCount, vehicleCount));
}

report.StrategyComparison = await RunStrategyComparisonAsync(1_000, 20);
report.ConcurrentRequests = await RunConcurrentRequestsAsync(requestCount: 8, tasksPerRequest: 500, vehicleCount: 10);
report.Retention = await RunRetentionTestAsync(iterations: 20, tasksPerRun: 1_000, vehicleCount: 20);
report.CapacityBenchmark = await RunCapacityBenchmarkAsync();
report.CompletedAtUtc = DateTime.UtcNow;
report.TotalDurationMilliseconds = (report.CompletedAtUtc - report.StartedAtUtc).TotalMilliseconds;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};
var jsonPath = Path.Combine(outputDirectory, "transport-loadtest-results.json");
var markdownPath = Path.Combine(outputDirectory, "transport-loadtest-summary.md");
await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, jsonOptions));
await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report));

Console.WriteLine();
Console.WriteLine(await File.ReadAllTextAsync(markdownPath));
Console.WriteLine($"JSON_RESULT={jsonPath}");
Console.WriteLine($"MARKDOWN_RESULT={markdownPath}");

static async Task<SingleRunCase> RunSingleCaseAsync(int taskCount, int vehicleCount)
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    await service.RunAsync(BuildScenario(25, 4, $"warmup-{taskCount}"), BaselinePolicy(), "loadtest");

    var samples = new List<RunSample>();
    for (var repetition = 1; repetition <= 3; repetition++)
    {
        var scenario = BuildScenario(taskCount, vehicleCount, $"single-{taskCount}-{repetition}");
        samples.Add(await MeasureRunAsync(service, scenario, BaselinePolicy()));
    }

    var ordered = samples.OrderBy(x => x.DurationMilliseconds).ToArray();
    var median = ordered[ordered.Length / 2];
    Console.WriteLine(
        $"single tasks={taskCount,5} vehicles={vehicleCount,3} median={median.DurationMilliseconds,10:F2} ms " +
        $"engine={median.EngineTasksPerSecond,10:F2} tasks/s allocated={median.AllocatedMegabytes,10:F2} MB");

    return new SingleRunCase
    {
        TaskCount = taskCount,
        VehicleCount = vehicleCount,
        Samples = samples,
        MedianDurationMilliseconds = median.DurationMilliseconds,
        MedianEngineTasksPerSecond = median.EngineTasksPerSecond,
        MedianAllocatedMegabytes = median.AllocatedMegabytes,
        MedianRetainedMegabytes = median.RetainedMegabytes,
        CompletedTaskCount = median.CompletedTaskCount,
        FailedTaskCount = median.FailedTaskCount,
        SimulatedThroughputPerHour = median.SimulatedThroughputPerHour,
        P95WaitingSeconds = median.P95WaitingSeconds,
        MaximumQueueLength = median.MaximumQueueLength
    };
}

static async Task<RunSample> MeasureRunAsync(
    ITransportSimulationService service,
    TransportSimulationScenario scenario,
    TransportSimulationPolicy policy)
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var process = Process.GetCurrentProcess();
    var workingSetBefore = process.WorkingSet64;
    var stopwatch = Stopwatch.StartNew();
    var run = await service.RunAsync(scenario, policy, "loadtest");
    stopwatch.Stop();
    var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
    var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
    process.Refresh();

    return new RunSample
    {
        DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        EngineTasksPerSecond = scenario.Tasks.Count / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001),
        AllocatedMegabytes = BytesToMegabytes(Math.Max(0, allocatedAfter - allocatedBefore)),
        RetainedMegabytes = BytesToMegabytes(Math.Max(0, memoryAfter - memoryBefore)),
        WorkingSetDeltaMegabytes = BytesToMegabytes(Math.Max(0, process.WorkingSet64 - workingSetBefore)),
        CompletedTaskCount = run.Metrics.CompletedTaskCount,
        FailedTaskCount = run.Metrics.FailedTaskCount,
        SimulatedThroughputPerHour = run.Metrics.ThroughputPerHour,
        P95WaitingSeconds = run.Metrics.P95WaitingSeconds,
        MaximumQueueLength = run.Metrics.MaximumQueueLength
    };
}

static async Task<StrategyComparisonResult> RunStrategyComparisonAsync(int taskCount, int vehicleCount)
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    await service.RunAsync(BuildScenario(25, 4, "compare-warmup"), BaselinePolicy(), "loadtest");
    var scenario = BuildScenario(taskCount, vehicleCount, "compare-1000");
    var policies = Enum.GetValues<TransportSimulationStrategyKind>()
        .Select(strategy => new TransportSimulationPolicy
        {
            PolicyId = $"POLICY-{strategy}",
            Name = strategy.ToString(),
            Strategy = strategy,
            FavorSameDestinationBatch = strategy == TransportSimulationStrategyKind.BalancedBatch
        })
        .ToArray();

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    var memoryBefore = GC.GetTotalMemory(true);
    var stopwatch = Stopwatch.StartNew();
    var result = await service.CompareAsync(scenario, policies, "loadtest");
    stopwatch.Stop();
    var allocatedAfter = GC.GetTotalAllocatedBytes(true);
    var memoryAfter = GC.GetTotalMemory(true);

    Console.WriteLine(
        $"compare tasks={taskCount} policies={policies.Length} duration={stopwatch.Elapsed.TotalMilliseconds:F2} ms " +
        $"recommended={result.RecommendedPolicyId}");

    return new StrategyComparisonResult
    {
        TaskCount = taskCount,
        PolicyCount = policies.Length,
        DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        EffectiveSimulationRunsPerSecond = policies.Length / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001),
        AllocatedMegabytes = BytesToMegabytes(Math.Max(0, allocatedAfter - allocatedBefore)),
        RetainedMegabytes = BytesToMegabytes(Math.Max(0, memoryAfter - memoryBefore)),
        RecommendedPolicyId = result.RecommendedPolicyId,
        RankedPolicies = result.Items.Select(x => new RankedPolicy
        {
            Rank = x.Rank,
            PolicyName = x.PolicyName,
            ObjectiveScore = x.Metrics.ObjectiveScore,
            ThroughputPerHour = x.Metrics.ThroughputPerHour,
            P95WaitingSeconds = x.Metrics.P95WaitingSeconds
        }).ToArray()
    };
}

static async Task<ConcurrentRequestResult> RunConcurrentRequestsAsync(
    int requestCount,
    int tasksPerRequest,
    int vehicleCount)
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    await service.RunAsync(BuildScenario(25, 4, "concurrency-warmup"), BaselinePolicy(), "loadtest");

    var durations = new double[requestCount];
    var total = Stopwatch.StartNew();
    var requests = Enumerable.Range(0, requestCount).Select(async index =>
    {
        var stopwatch = Stopwatch.StartNew();
        await service.RunAsync(
            BuildScenario(tasksPerRequest, vehicleCount, $"parallel-{index}"),
            BaselinePolicy($"parallel-policy-{index}"),
            "loadtest");
        stopwatch.Stop();
        durations[index] = stopwatch.Elapsed.TotalMilliseconds;
    }).ToArray();
    await Task.WhenAll(requests);
    total.Stop();

    Array.Sort(durations);
    var p95Index = Math.Clamp((int)Math.Ceiling(durations.Length * 0.95) - 1, 0, durations.Length - 1);
    Console.WriteLine(
        $"parallel requests={requestCount} tasks/request={tasksPerRequest} total={total.Elapsed.TotalMilliseconds:F2} ms " +
        $"p95={durations[p95Index]:F2} ms");

    return new ConcurrentRequestResult
    {
        RequestCount = requestCount,
        TasksPerRequest = tasksPerRequest,
        TotalDurationMilliseconds = total.Elapsed.TotalMilliseconds,
        RequestsPerSecond = requestCount / Math.Max(total.Elapsed.TotalSeconds, 0.000001),
        MinimumRequestMilliseconds = durations[0],
        MedianRequestMilliseconds = durations[durations.Length / 2],
        P95RequestMilliseconds = durations[p95Index],
        MaximumRequestMilliseconds = durations[^1],
        Observation = "同一仿真服务实例使用单通道 SemaphoreSlim；并发请求安全排队，不会并行占满 CPU。"
    };
}

static async Task<RetentionResult> RunRetentionTestAsync(
    int iterations,
    int tasksPerRun,
    int vehicleCount)
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    await service.RunAsync(BuildScenario(25, 4, "retention-warmup"), BaselinePolicy(), "loadtest");
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    var memoryBefore = GC.GetTotalMemory(true);
    var process = Process.GetCurrentProcess();
    var workingSetBefore = process.WorkingSet64;
    var stopwatch = Stopwatch.StartNew();

    for (var index = 0; index < iterations; index++)
    {
        await service.RunAsync(
            BuildScenario(tasksPerRun, vehicleCount, $"retention-{index}"),
            BaselinePolicy($"retention-policy-{index}"),
            "loadtest");
    }

    stopwatch.Stop();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    var memoryAfter = GC.GetTotalMemory(true);
    process.Refresh();
    var retainedRuns = service.GetRuns(1000).Count;

    Console.WriteLine(
        $"retention iterations={iterations} tasks/run={tasksPerRun} duration={stopwatch.Elapsed.TotalMilliseconds:F2} ms " +
        $"managed-growth={BytesToMegabytes(memoryAfter - memoryBefore):F2} MB retained-runs={retainedRuns}");

    return new RetentionResult
    {
        Iterations = iterations,
        TasksPerRun = tasksPerRun,
        TotalTaskResultsRetained = iterations * tasksPerRun,
        RetainedRunCount = retainedRuns,
        DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        ManagedMemoryGrowthMegabytes = BytesToMegabytes(memoryAfter - memoryBefore),
        WorkingSetGrowthMegabytes = BytesToMegabytes(process.WorkingSet64 - workingSetBefore),
        ApproximateManagedBytesPerTaskResult = iterations * tasksPerRun == 0
            ? 0
            : Math.Max(0, memoryAfter - memoryBefore) / (double)(iterations * tasksPerRun)
    };
}

static async Task<CapacityBenchmarkResult> RunCapacityBenchmarkAsync()
{
    using var provider = CreateProvider();
    var service = provider.GetRequiredService<ITransportSimulationService>();
    var request = new TransportCapacityBenchmarkRequest
    {
        Name = "temporary-github-runner-capacity",
        DurationMinutes = 30,
        VehicleCounts = new[] { 5, 10, 20 },
        TaskRatesPerHour = new[] { 120, 240, 480 },
        Repetitions = 2,
        Seed = 20260723,
        Policy = BaselinePolicy("capacity-baseline")
    };

    var stopwatch = Stopwatch.StartNew();
    var result = await service.RunCapacityBenchmarkAsync(request, "loadtest");
    stopwatch.Stop();
    Console.WriteLine(
        $"capacity grid={result.Points.Count} duration={stopwatch.Elapsed.TotalMilliseconds:F2} ms " +
        $"max-sustainable={result.MaximumSustainableTaskRatePerHour}/h vehicles={result.RecommendedVehicleCount}");

    return new CapacityBenchmarkResult
    {
        GridPointCount = result.Points.Count,
        Repetitions = request.Repetitions,
        DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        MaximumSustainableTaskRatePerHour = result.MaximumSustainableTaskRatePerHour,
        RecommendedVehicleCount = result.RecommendedVehicleCount,
        Conclusion = result.Conclusion,
        Points = result.Points
    };
}

static ServiceProvider CreateProvider()
{
    var services = new ServiceCollection();
    services.AddSingleton<IAlarmCenter>(new AlarmCenter(new EventBus()));
    services.AddUnifiedTransportScheduling();
    return services.BuildServiceProvider();
}

static TransportSimulationScenario BuildScenario(int taskCount, int vehicleCount, string name)
{
    var stationCount = Math.Clamp(vehicleCount / 2, 2, 20);
    var vehicles = Enumerable.Range(0, vehicleCount).Select(index => new TransportSimulationVehicle
    {
        VehicleId = $"EMS-{index + 1:000}",
        Kind = TransportVehicleKind.Ems,
        Online = true,
        BatteryPercent = 70 + index % 31,
        InitialAvailableOffsetSeconds = index % 10
    }).ToArray();
    var stations = Enumerable.Range(0, stationCount).Select(index => new TransportSimulationStation
    {
        StationId = $"ST-{index + 1:00}",
        Capacity = 1 + index % 3,
        AdditionalServiceSeconds = index % 5,
        Enabled = true
    }).ToArray();
    var tasks = Enumerable.Range(0, taskCount).Select(index =>
    {
        var arrival = index % 1800;
        return new TransportSimulationTask
        {
            TaskId = $"{name}-T-{index + 1:00000}",
            SourceNodeId = $"N-{index % 100:000}",
            DestinationNodeId = $"N-{(index * 7 + 13) % 100:000}",
            DestinationStationId = stations[index % stations.Length].StationId,
            ResourceIds = new[]
            {
                $"R-{index % 200:000}",
                $"R-{(index + 1) % 200:000}"
            },
            RequiredVehicleKind = TransportVehicleKind.Ems,
            Priority = index % 100,
            ProductionOrderPriority = index % 20,
            IsRecoveryTask = index % 97 == 0,
            ArrivalOffsetSeconds = arrival,
            DeadlineOffsetSeconds = arrival + 180 + index % 900,
            EstimatedTravelSeconds = 20 + index % 100,
            ServiceSeconds = 5 + index % 20
        };
    }).ToArray();

    return new TransportSimulationScenario
    {
        ScenarioId = name,
        Name = name,
        Description = "GitHub Actions isolated transport simulation load test",
        Source = TransportSimulationSource.Manual,
        BaseTimeUtc = new DateTime(2026, 7, 23, 0, 0, 0, DateTimeKind.Utc),
        HorizonSeconds = 6 * 60 * 60,
        Seed = 20260723,
        Tasks = tasks,
        Vehicles = vehicles,
        Stations = stations
    };
}

static TransportSimulationPolicy BaselinePolicy(string id = "baseline") => new()
{
    PolicyId = id,
    Name = id,
    Strategy = TransportSimulationStrategyKind.BaselineDynamicPriority,
    AgingPointsPerMinute = 2,
    DeadlineUrgencyPoints = 30,
    RecoveryTaskBoost = 50,
    CongestionPenaltyPerQueuedTask = 3,
    MaximumBatchSize = 5,
    MinimumBatteryPercent = 20
};

static double BytesToMegabytes(long bytes) => bytes / 1024d / 1024d;

static string BuildMarkdown(LoadTestReport report)
{
    var builder = new StringBuilder();
    builder.AppendLine("# EMS/RGV 离线仿真压力测试结果");
    builder.AppendLine();
    builder.AppendLine($"- 时间：{report.StartedAtUtc:O} - {report.CompletedAtUtc:O}");
    builder.AppendLine($"- 环境：{report.OperatingSystem}");
    builder.AppendLine($"- .NET：{report.Framework}");
    builder.AppendLine($"- 逻辑 CPU：{report.ProcessorCount}");
    builder.AppendLine($"- Server GC：{report.IsServerGc}");
    builder.AppendLine();
    builder.AppendLine("## 单次仿真规模");
    builder.AppendLine();
    builder.AppendLine("|任务数|车辆数|中位耗时 ms|引擎 tasks/s|分配 MB|保留 MB|完成|失败|P95 等待 s|最大队列|");
    builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var item in report.SingleRunCases)
    {
        builder.AppendLine($"|{item.TaskCount}|{item.VehicleCount}|{item.MedianDurationMilliseconds:F2}|{item.MedianEngineTasksPerSecond:F2}|{item.MedianAllocatedMegabytes:F2}|{item.MedianRetainedMegabytes:F2}|{item.CompletedTaskCount}|{item.FailedTaskCount}|{item.P95WaitingSeconds:F2}|{item.MaximumQueueLength}|");
    }
    builder.AppendLine();
    builder.AppendLine("## 复合场景");
    builder.AppendLine();
    builder.AppendLine($"- 五策略 A/B（{report.StrategyComparison.TaskCount} 任务）：{report.StrategyComparison.DurationMilliseconds:F2} ms，推荐 {report.StrategyComparison.RecommendedPolicyId}。");
    builder.AppendLine($"- 8 路并发 × 500 任务：总耗时 {report.ConcurrentRequests.TotalDurationMilliseconds:F2} ms，P95 请求 {report.ConcurrentRequests.P95RequestMilliseconds:F2} ms，{report.ConcurrentRequests.Observation}");
    builder.AppendLine($"- 历史保留：{report.Retention.Iterations} 次 × {report.Retention.TasksPerRun} 任务，托管内存增长 {report.Retention.ManagedMemoryGrowthMegabytes:F2} MB，约 {report.Retention.ApproximateManagedBytesPerTaskResult:F2} bytes/任务结果。");
    builder.AppendLine($"- 容量网格：{report.CapacityBenchmark.GridPointCount} 点 × {report.CapacityBenchmark.Repetitions} 次，耗时 {report.CapacityBenchmark.DurationMilliseconds:F2} ms，最大可持续任务率 {report.CapacityBenchmark.MaximumSustainableTaskRatePerHour}/h，建议车辆数 {report.CapacityBenchmark.RecommendedVehicleCount}。");
    builder.AppendLine();
    builder.AppendLine("## 说明");
    builder.AppendLine();
    builder.AppendLine("本结果衡量第十二阶段纯内存离散事件仿真内核，不包含 SQL Server、HTTP、SignalR、真实 PLC 通信、网络抖动或 Windows IIS。现场容量仍需使用实际 PLC 周期、数据库和设备节拍复测。");
    return builder.ToString();
}

public sealed record LoadTestReport
{
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; set; }
    public double TotalDurationMilliseconds { get; set; }
    public string MachineName { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Framework { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public bool IsServerGc { get; init; }
    public List<SingleRunCase> SingleRunCases { get; } = new();
    public StrategyComparisonResult StrategyComparison { get; set; } = new();
    public ConcurrentRequestResult ConcurrentRequests { get; set; } = new();
    public RetentionResult Retention { get; set; } = new();
    public CapacityBenchmarkResult CapacityBenchmark { get; set; } = new();
}

public sealed record SingleRunCase
{
    public int TaskCount { get; init; }
    public int VehicleCount { get; init; }
    public IReadOnlyList<RunSample> Samples { get; init; } = Array.Empty<RunSample>();
    public double MedianDurationMilliseconds { get; init; }
    public double MedianEngineTasksPerSecond { get; init; }
    public double MedianAllocatedMegabytes { get; init; }
    public double MedianRetainedMegabytes { get; init; }
    public int CompletedTaskCount { get; init; }
    public int FailedTaskCount { get; init; }
    public double SimulatedThroughputPerHour { get; init; }
    public double P95WaitingSeconds { get; init; }
    public int MaximumQueueLength { get; init; }
}

public sealed record RunSample
{
    public double DurationMilliseconds { get; init; }
    public double EngineTasksPerSecond { get; init; }
    public double AllocatedMegabytes { get; init; }
    public double RetainedMegabytes { get; init; }
    public double WorkingSetDeltaMegabytes { get; init; }
    public int CompletedTaskCount { get; init; }
    public int FailedTaskCount { get; init; }
    public double SimulatedThroughputPerHour { get; init; }
    public double P95WaitingSeconds { get; init; }
    public int MaximumQueueLength { get; init; }
}

public sealed record StrategyComparisonResult
{
    public int TaskCount { get; init; }
    public int PolicyCount { get; init; }
    public double DurationMilliseconds { get; init; }
    public double EffectiveSimulationRunsPerSecond { get; init; }
    public double AllocatedMegabytes { get; init; }
    public double RetainedMegabytes { get; init; }
    public string? RecommendedPolicyId { get; init; }
    public IReadOnlyList<RankedPolicy> RankedPolicies { get; init; } = Array.Empty<RankedPolicy>();
}

public sealed record RankedPolicy
{
    public int Rank { get; init; }
    public string PolicyName { get; init; } = string.Empty;
    public double ObjectiveScore { get; init; }
    public double ThroughputPerHour { get; init; }
    public double P95WaitingSeconds { get; init; }
}

public sealed record ConcurrentRequestResult
{
    public int RequestCount { get; init; }
    public int TasksPerRequest { get; init; }
    public double TotalDurationMilliseconds { get; init; }
    public double RequestsPerSecond { get; init; }
    public double MinimumRequestMilliseconds { get; init; }
    public double MedianRequestMilliseconds { get; init; }
    public double P95RequestMilliseconds { get; init; }
    public double MaximumRequestMilliseconds { get; init; }
    public string Observation { get; init; } = string.Empty;
}

public sealed record RetentionResult
{
    public int Iterations { get; init; }
    public int TasksPerRun { get; init; }
    public int TotalTaskResultsRetained { get; init; }
    public int RetainedRunCount { get; init; }
    public double DurationMilliseconds { get; init; }
    public double ManagedMemoryGrowthMegabytes { get; init; }
    public double WorkingSetGrowthMegabytes { get; init; }
    public double ApproximateManagedBytesPerTaskResult { get; init; }
}

public sealed record CapacityBenchmarkResult
{
    public int GridPointCount { get; init; }
    public int Repetitions { get; init; }
    public double DurationMilliseconds { get; init; }
    public int MaximumSustainableTaskRatePerHour { get; init; }
    public int RecommendedVehicleCount { get; init; }
    public string Conclusion { get; init; } = string.Empty;
    public IReadOnlyList<TransportCapacityBenchmarkPoint> Points { get; init; } = Array.Empty<TransportCapacityBenchmarkPoint>();
}
