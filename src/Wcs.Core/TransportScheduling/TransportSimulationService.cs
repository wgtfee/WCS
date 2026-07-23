namespace Wcs.Core.TransportScheduling;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public interface ITransportSimulationService
{
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task<TransportSimulationScenario> BuildCurrentScenarioAsync(
        string name,
        int horizonSeconds = 3600,
        int maximumTasks = 1000,
        CancellationToken cancellationToken = default);
    Task<TransportSimulationScenario> BuildHistoricalScenarioAsync(
        TransportHistoricalReplayRequest request,
        CancellationToken cancellationToken = default);
    Task<TransportSimulationRun> RunAsync(
        TransportSimulationScenario scenario,
        TransportSimulationPolicy policy,
        string initiatedBy,
        CancellationToken cancellationToken = default);
    Task<TransportStrategyComparisonReport> CompareAsync(
        TransportSimulationScenario scenario,
        IReadOnlyList<TransportSimulationPolicy> policies,
        string initiatedBy,
        CancellationToken cancellationToken = default);
    Task<TransportBatchOptimizationResult> OptimizeBatchAsync(
        TransportSimulationScenario scenario,
        string initiatedBy,
        CancellationToken cancellationToken = default);
    Task<TransportCapacityBenchmarkReport> RunCapacityBenchmarkAsync(
        TransportCapacityBenchmarkRequest request,
        string initiatedBy,
        CancellationToken cancellationToken = default);
    Task<TransportFinalAcceptanceReport> GenerateAcceptanceReportAsync(
        string name,
        string simulationRunId,
        TransportAcceptanceCriteria criteria,
        string initiatedBy,
        string? comparisonId = null,
        string? benchmarkId = null,
        CancellationToken cancellationToken = default);
    IReadOnlyList<TransportSimulationRun> GetRuns(int maxCount = 100);
    IReadOnlyList<TransportStrategyComparisonReport> GetComparisons(int maxCount = 100);
    IReadOnlyList<TransportBatchOptimizationResult> GetOptimizations(int maxCount = 100);
    IReadOnlyList<TransportCapacityBenchmarkReport> GetBenchmarks(int maxCount = 100);
    IReadOnlyList<TransportFinalAcceptanceReport> GetAcceptanceReports(int maxCount = 100);
    TransportSimulationSummary GetSummary();
}

public sealed class TransportSimulationService : ITransportSimulationService
{
    private readonly ITransportJournalStore _journal;
    private readonly ITransportProductionDispatchService _production;
    private readonly ITransportVehicleRegistry _vehicles;
    private readonly ITransportStationCongestionService _stations;
    private readonly ITransportSingleTrackCoordinator _singleTrack;
    private readonly ITransportProductionTuningService _tuning;
    private readonly ITransportTelemetryService _telemetry;
    private readonly TransportSimulationOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<TransportSimulationRun> _runs = new();
    private readonly List<TransportStrategyComparisonReport> _comparisons = new();
    private readonly List<TransportBatchOptimizationResult> _optimizations = new();
    private readonly List<TransportCapacityBenchmarkReport> _benchmarks = new();
    private readonly List<TransportFinalAcceptanceReport> _acceptanceReports = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public TransportSimulationService(
        ITransportJournalStore journal,
        ITransportProductionDispatchService production,
        ITransportVehicleRegistry vehicles,
        ITransportStationCongestionService stations,
        ITransportSingleTrackCoordinator singleTrack,
        ITransportProductionTuningService tuning,
        ITransportTelemetryService telemetry,
        TransportSimulationOptions options)
    {
        _journal = journal;
        _production = production;
        _vehicles = vehicles;
        _stations = stations;
        _singleTrack = singleTrack;
        _tuning = tuning;
        _telemetry = telemetry;
        _options = options;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var runs = await LoadRecordsAsync<TransportSimulationRun>(
            TransportJournalCategory.SimulationRun,
            _options.MaximumStoredRuns,
            cancellationToken).ConfigureAwait(false);
        var comparisons = await LoadRecordsAsync<TransportStrategyComparisonReport>(
            TransportJournalCategory.StrategyComparison,
            _options.MaximumStoredComparisons,
            cancellationToken).ConfigureAwait(false);
        var optimizations = await LoadRecordsAsync<TransportBatchOptimizationResult>(
            TransportJournalCategory.OptimizationRecommendation,
            _options.MaximumStoredComparisons,
            cancellationToken).ConfigureAwait(false);
        var benchmarks = await LoadRecordsAsync<TransportCapacityBenchmarkReport>(
            TransportJournalCategory.CapacityBenchmark,
            _options.MaximumStoredComparisons,
            cancellationToken).ConfigureAwait(false);
        var acceptance = await LoadRecordsAsync<TransportFinalAcceptanceReport>(
            TransportJournalCategory.FinalAcceptanceReport,
            _options.MaximumStoredComparisons,
            cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            ReplaceUnsafe(_runs, runs.OrderBy(x => x.CompletedAtUtc), _options.MaximumStoredRuns);
            ReplaceUnsafe(_comparisons, comparisons.OrderBy(x => x.CompletedAtUtc), _options.MaximumStoredComparisons);
            ReplaceUnsafe(_optimizations, optimizations.OrderBy(x => x.GeneratedAtUtc), _options.MaximumStoredComparisons);
            ReplaceUnsafe(_benchmarks, benchmarks.OrderBy(x => x.CompletedAtUtc), _options.MaximumStoredComparisons);
            ReplaceUnsafe(_acceptanceReports, acceptance.OrderBy(x => x.GeneratedAtUtc), _options.MaximumStoredComparisons);
        }
    }

    public Task<TransportSimulationScenario> BuildCurrentScenarioAsync(
        string name,
        int horizonSeconds = 3600,
        int maximumTasks = 1000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateName(name);
        horizonSeconds = Math.Clamp(horizonSeconds, 60, 7 * 24 * 3600);
        maximumTasks = Math.Clamp(maximumTasks, 1, _options.MaximumScenarioTasks);
        var queue = _production.GetQueue()
            .Where(x => x.State is not (TransportProductionQueueState.Cancelled or TransportProductionQueueState.Assigned))
            .Take(maximumTasks)
            .ToArray();
        var baseTime = queue.Length == 0
            ? DateTime.UtcNow
            : queue.Min(x => x.ProductionRequest.EnqueuedAtUtc);
        return Task.FromResult(new TransportSimulationScenario
        {
            Name = name.Trim(),
            Description = "从当前生产队列、车辆和站点快照生成的离线仿真场景",
            Source = TransportSimulationSource.CurrentSnapshot,
            BaseTimeUtc = baseTime,
            HorizonSeconds = horizonSeconds,
            Tasks = queue.Select(x => ConvertTask(x.ProductionRequest, baseTime)).ToArray(),
            Vehicles = BuildVehicleSnapshot(),
            Stations = BuildStationSnapshot(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    public async Task<TransportSimulationScenario> BuildHistoricalScenarioAsync(
        TransportHistoricalReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(request.Name);
        if (request.ToUtc < request.FromUtc)
            throw new ArgumentException("ToUtc 不能早于 FromUtc", nameof(request));
        var maximumTasks = Math.Clamp(request.MaximumTasks, 1, _options.MaximumScenarioTasks);
        var records = await _journal.QueryAsync(
            TransportJournalCategory.ProductionQueue,
            Math.Clamp(_options.HistoricalJournalLimit, maximumTasks, 50000),
            cancellationToken).ConfigureAwait(false);
        var queue = records
            .Where(x => x.OccurredAtUtc >= request.FromUtc && x.OccurredAtUtc <= request.ToUtc)
            .Select(x => Deserialize<TransportProductionQueueItem>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<TransportProductionQueueItem>()
            .GroupBy(x => x.ProductionRequest.Request.RequestId, StringComparer.Ordinal)
            .Select(x => x.OrderByDescending(y => y.UpdatedAtUtc).First())
            .OrderBy(x => x.ProductionRequest.EnqueuedAtUtc)
            .Take(maximumTasks)
            .ToArray();
        var baseTime = queue.Length == 0 ? request.FromUtc : queue.Min(x => x.ProductionRequest.EnqueuedAtUtc);
        var vehicles = BuildVehicleSnapshot();
        if (vehicles.Count == 0)
        {
            vehicles = queue
                .Where(x => !string.IsNullOrWhiteSpace(x.AssignedVehicleId))
                .Select(x => x.AssignedVehicleId!)
                .Distinct(StringComparer.Ordinal)
                .Select(x => new TransportSimulationVehicle
                {
                    VehicleId = x,
                    Kind = TransportVehicleKind.Ems,
                    Online = true
                })
                .ToArray();
        }
        return new TransportSimulationScenario
        {
            Name = request.Name.Trim(),
            Description = $"历史生产队列回放：{request.FromUtc:O} - {request.ToUtc:O}",
            Source = TransportSimulationSource.HistoricalReplay,
            BaseTimeUtc = baseTime,
            HistoricalFromUtc = request.FromUtc,
            HistoricalToUtc = request.ToUtc,
            HorizonSeconds = Math.Clamp(
                (int)Math.Ceiling((request.ToUtc - request.FromUtc).TotalSeconds),
                60,
                30 * 24 * 3600),
            Tasks = queue.Select(x => ConvertTask(
                x.ProductionRequest,
                baseTime,
                Math.Max(1, request.DefaultTravelSeconds),
                Math.Max(0, request.DefaultServiceSeconds))).ToArray(),
            Vehicles = vehicles,
            Stations = BuildStationSnapshot(),
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public async Task<TransportSimulationRun> RunAsync(
        TransportSimulationScenario scenario,
        TransportSimulationPolicy policy,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateScenario(scenario);
        ValidatePolicy(policy);
        ValidateActor(initiatedBy);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.Simulation,
            "transport.simulation.run",
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scenario.id"] = scenario.ScenarioId,
                ["policy.id"] = policy.PolicyId,
                ["simulation.source"] = scenario.Source.ToString()
            });
        try
        {
            var run = ExecuteCore(scenario, policy, initiatedBy, cancellationToken);
            await PersistAsync(
                TransportJournalCategory.SimulationRun,
                run.RunId,
                run,
                run.CompletedAtUtc,
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _runs.Add(run);
                TrimUnsafe(_runs, _options.MaximumStoredRuns);
            }
            operation.Complete(true, $"离线仿真完成，吞吐 {run.Metrics.ThroughputPerHour:F2}/h，P95 等待 {run.Metrics.P95WaitingSeconds:F2}s");
            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransportStrategyComparisonReport> CompareAsync(
        TransportSimulationScenario scenario,
        IReadOnlyList<TransportSimulationPolicy> policies,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateScenario(scenario);
        ValidateActor(initiatedBy);
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Count is < 2 or > 10)
            throw new ArgumentException("策略对比数量必须在 2 到 10 之间", nameof(policies));
        foreach (var policy in policies)
            ValidatePolicy(policy);

        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.StrategyComparison,
            "transport.simulation.compare",
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scenario.id"] = scenario.ScenarioId,
                ["policy.count"] = policies.Count.ToString()
            });
        var runs = new List<(TransportSimulationPolicy Policy, TransportSimulationRun Run)>();
        foreach (var policy in policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            runs.Add((policy, await RunAsync(scenario, policy, initiatedBy, cancellationToken).ConfigureAwait(false)));
        }
        var ranked = runs
            .OrderByDescending(x => x.Run.Metrics.ObjectiveScore)
            .ThenByDescending(x => x.Run.Metrics.ThroughputPerHour)
            .ThenBy(x => x.Run.Metrics.P95WaitingSeconds)
            .Select((x, index) => new TransportStrategyComparisonItem
            {
                PolicyId = x.Policy.PolicyId,
                PolicyName = x.Policy.Name,
                Strategy = x.Policy.Strategy,
                RunId = x.Run.RunId,
                Metrics = x.Run.Metrics,
                Rank = index + 1
            })
            .ToArray();
        var winner = ranked[0];
        var baseline = ranked.FirstOrDefault(x => x.Strategy == TransportSimulationStrategyKind.BaselineDynamicPriority)
            ?? ranked[^1];
        var report = new TransportStrategyComparisonReport
        {
            ScenarioId = scenario.ScenarioId,
            ScenarioName = scenario.Name,
            InitiatedBy = initiatedBy.Trim(),
            Items = ranked,
            RecommendedPolicyId = winner.PolicyId,
            Recommendation = BuildComparisonRecommendation(winner, baseline),
            CompletedAtUtc = DateTime.UtcNow
        };
        await PersistAsync(
            TransportJournalCategory.StrategyComparison,
            report.ComparisonId,
            report,
            report.CompletedAtUtc,
            cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _comparisons.Add(report);
            TrimUnsafe(_comparisons, _options.MaximumStoredComparisons);
        }
        operation.Complete(true, $"策略对比完成，推荐 {winner.PolicyName}");
        return report;
    }

    public async Task<TransportBatchOptimizationResult> OptimizeBatchAsync(
        TransportSimulationScenario scenario,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateScenario(scenario);
        ValidateActor(initiatedBy);
        var current = _tuning.Current;
        var policies = new[]
        {
            CreatePolicy("当前动态优先级", TransportSimulationStrategyKind.BaselineDynamicPriority, current, false),
            CreatePolicy("老化优先", TransportSimulationStrategyKind.AgingFirst, current, false),
            CreatePolicy("交期优先", TransportSimulationStrategyKind.DeadlineFirst, current, false),
            CreatePolicy("拥堵感知", TransportSimulationStrategyKind.CongestionAware, current, false),
            CreatePolicy("同目的地批量均衡", TransportSimulationStrategyKind.BalancedBatch, current, true)
        };
        var comparison = await CompareAsync(scenario, policies, initiatedBy, cancellationToken).ConfigureAwait(false);
        var recommendedItem = comparison.Items.First(x => x.Rank == 1);
        var policy = policies.First(x => x.PolicyId == recommendedItem.PolicyId);
        var run = GetRuns(_options.MaximumStoredRuns).First(x => x.RunId == recommendedItem.RunId);
        var result = new TransportBatchOptimizationResult
        {
            ScenarioId = scenario.ScenarioId,
            RecommendedPolicy = policy,
            RecommendedTaskOrder = run.Tasks
                .OrderBy(x => x.DispatchOffsetSeconds)
                .ThenByDescending(x => x.EffectivePriority)
                .Select(x => x.TaskId)
                .ToArray(),
            ObjectiveScore = run.Metrics.ObjectiveScore,
            Explanation = $"在 5 种候选策略中，{policy.Name} 的目标得分最高；该结果仅为离线候选，不会自动修改生产整定参数。",
            GeneratedAtUtc = DateTime.UtcNow
        };
        await PersistAsync(
            TransportJournalCategory.OptimizationRecommendation,
            result.OptimizationId,
            result,
            result.GeneratedAtUtc,
            cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _optimizations.Add(result);
            TrimUnsafe(_optimizations, _options.MaximumStoredComparisons);
        }
        return result;
    }

    public async Task<TransportCapacityBenchmarkReport> RunCapacityBenchmarkAsync(
        TransportCapacityBenchmarkRequest request,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(request.Name);
        ValidateActor(initiatedBy);
        ValidatePolicy(request.Policy);
        var durationMinutes = Math.Clamp(request.DurationMinutes, 5, 24 * 60);
        var vehicleCounts = request.VehicleCounts.Distinct().Where(x => x > 0).OrderBy(x => x).Take(20).ToArray();
        var taskRates = request.TaskRatesPerHour.Distinct().Where(x => x > 0).OrderBy(x => x).Take(30).ToArray();
        var repetitions = Math.Clamp(request.Repetitions, 1, 10);
        if (vehicleCounts.Length == 0 || taskRates.Length == 0)
            throw new ArgumentException("车辆数量和任务率至少各包含一个正整数", nameof(request));

        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.CapacityBenchmark,
            "transport.simulation.capacity",
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vehicle.points"] = vehicleCounts.Length.ToString(),
                ["rate.points"] = taskRates.Length.ToString(),
                ["repetitions"] = repetitions.ToString()
            });
        var points = new List<TransportCapacityBenchmarkPoint>();
        foreach (var vehicleCount in vehicleCounts)
        {
            foreach (var taskRate in taskRates)
            {
                var metrics = new List<CapacityBenchmarkSample>();
                for (var repetition = 0; repetition < repetitions; repetition++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scenario = BuildStressScenario(
                        request,
                        durationMinutes,
                        vehicleCount,
                        taskRate,
                        repetition);
                    var run = ExecuteCore(
                        scenario,
                        request.Policy,
                        initiatedBy,
                        cancellationToken,
                        drainAfterHorizon: true);
                    metrics.Add(BuildCapacityBenchmarkSample(
                        run,
                        scenario.HorizonSeconds,
                        scenario.Vehicles.Count));
                }
                var point = new TransportCapacityBenchmarkPoint
                {
                    VehicleCount = vehicleCount,
                    TaskRatePerHour = taskRate,
                    AverageCompletedTasks = Math.Round(metrics.Average(x => x.CompletedTaskCount), 2),
                    AverageArrivedTasks = Math.Round(metrics.Average(x => x.ArrivedTaskCount), 2),
                    AverageOutstandingTasksAtCutoff = Math.Round(metrics.Average(x => x.OutstandingTaskCount), 2),
                    AverageFailedTasks = Math.Round(metrics.Average(x => x.FailedTaskCount), 2),
                    AverageThroughputPerHour = Math.Round(metrics.Average(x => x.ThroughputPerHour), 2),
                    AverageP95WaitingSeconds = Math.Round(metrics.Average(x => x.P95WaitingSeconds), 2),
                    AverageDeadlineMissRatePercent = Math.Round(metrics.Average(x => x.DeadlineMissRatePercent), 2),
                    AverageFleetUtilizationPercent = Math.Round(metrics.Average(x => x.FleetUtilizationPercent), 2),
                    Sustainable = metrics.Average(x => x.P95WaitingSeconds) <= _options.SustainableP95WaitingSeconds &&
                                  metrics.Average(x => x.DeadlineMissRatePercent) <= _options.SustainableDeadlineMissRatePercent &&
                                  metrics.All(x => x.FailedTaskCount == 0)
                };
                points.Add(point);
            }
        }
        var sustainable = points.Where(x => x.Sustainable).ToArray();
        var maximumRate = sustainable.Length == 0 ? 0 : sustainable.Max(x => x.TaskRatePerHour);
        var recommendedVehicleCount = sustainable
            .Where(x => x.TaskRatePerHour == maximumRate)
            .OrderBy(x => x.VehicleCount)
            .Select(x => x.VehicleCount)
            .FirstOrDefault();
        var report = new TransportCapacityBenchmarkReport
        {
            Name = request.Name.Trim(),
            InitiatedBy = initiatedBy.Trim(),
            Points = points,
            MaximumSustainableTaskRatePerHour = maximumRate,
            RecommendedVehicleCount = recommendedVehicleCount,
            Conclusion = maximumRate == 0
                ? "当前组合未达到可持续门槛，需要增加车辆、降低任务率或调整站点能力。"
                : $"在当前门槛下，最大可持续任务率为 {maximumRate}/h，达到该能力的最少车辆数为 {recommendedVehicleCount}。",
            CompletedAtUtc = DateTime.UtcNow
        };
        await PersistAsync(
            TransportJournalCategory.CapacityBenchmark,
            report.BenchmarkId,
            report,
            report.CompletedAtUtc,
            cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _benchmarks.Add(report);
            TrimUnsafe(_benchmarks, _options.MaximumStoredComparisons);
        }
        operation.Complete(true, report.Conclusion);
        return report;
    }

    public async Task<TransportFinalAcceptanceReport> GenerateAcceptanceReportAsync(
        string name,
        string simulationRunId,
        TransportAcceptanceCriteria criteria,
        string initiatedBy,
        string? comparisonId = null,
        string? benchmarkId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ValidateActor(initiatedBy);
        ArgumentNullException.ThrowIfNull(criteria);
        TransportSimulationRun run;
        lock (_sync)
        {
            run = _runs.FirstOrDefault(x => x.RunId == simulationRunId)
                ?? throw new KeyNotFoundException($"仿真运行 {simulationRunId} 不存在");
        }
        var failureRate = run.Metrics.TotalTaskCount == 0
            ? 0
            : run.Metrics.FailedTaskCount * 100d / run.Metrics.TotalTaskCount;
        var checks = new[]
        {
            MinimumCheck("吞吐能力", run.Metrics.ThroughputPerHour, criteria.MinimumThroughputPerHour, "任务/小时"),
            MaximumCheck("P95 等待时间", run.Metrics.P95WaitingSeconds, criteria.MaximumP95WaitingSeconds, "秒"),
            MaximumCheck("交期违约率", run.Metrics.DeadlineMissRatePercent, criteria.MaximumDeadlineMissRatePercent, "%"),
            MaximumCheck("任务失败率", failureRate, criteria.MaximumFailureRatePercent, "%"),
            MaximumCheck("车队利用率", run.Metrics.FleetUtilizationPercent, criteria.MaximumFleetUtilizationPercent, "%"),
            MaximumCheck("最大队列长度", run.Metrics.MaximumQueueLength, criteria.MaximumQueueLength, "项")
        };
        var failedCount = checks.Count(x => !x.Passed);
        var state = failedCount == 0
            ? TransportAcceptanceState.Passed
            : failedCount <= 2
                ? TransportAcceptanceState.Conditional
                : TransportAcceptanceState.Failed;
        var report = new TransportFinalAcceptanceReport
        {
            Name = name.Trim(),
            InitiatedBy = initiatedBy.Trim(),
            SimulationRunId = simulationRunId,
            ComparisonId = comparisonId,
            BenchmarkId = benchmarkId,
            State = state,
            Checks = checks,
            RequiredManualChecks = new[]
            {
                "核对现场 PLC 程序版本、点位表版本和 WCS 点位映射版本一致",
                "执行所有车辆急停、断线、心跳冻结和恢复流程的实车演练",
                "确认单轨区段、路口、站点和物理闭塞范围与现场一致",
                "在生产班次执行蓝绿切换、Drain、回退和数据库恢复演练",
                "确认操作员权限、双人审批、审计日志和备份下载流程有效",
                "由设备、工艺、生产和信息化负责人共同签署最终上线清单"
            },
            Conclusion = state switch
            {
                TransportAcceptanceState.Passed => "离线指标全部达到门槛；完成全部现场人工检查后可进入正式投产审批。",
                TransportAcceptanceState.Conditional => $"存在 {failedCount} 项离线指标未达门槛；整改并复测后可进入条件验收。",
                _ => $"存在 {failedCount} 项关键指标未达门槛，当前不具备正式投产条件。"
            },
            GeneratedAtUtc = DateTime.UtcNow
        };
        await PersistAsync(
            TransportJournalCategory.FinalAcceptanceReport,
            report.ReportId,
            report,
            report.GeneratedAtUtc,
            cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _acceptanceReports.Add(report);
            TrimUnsafe(_acceptanceReports, _options.MaximumStoredComparisons);
        }
        return report;
    }

    public IReadOnlyList<TransportSimulationRun> GetRuns(int maxCount = 100)
    {
        lock (_sync)
            return _runs.OrderByDescending(x => x.CompletedAtUtc).Take(ClampCount(maxCount, _options.MaximumStoredRuns)).ToArray();
    }

    public IReadOnlyList<TransportStrategyComparisonReport> GetComparisons(int maxCount = 100)
    {
        lock (_sync)
            return _comparisons.OrderByDescending(x => x.CompletedAtUtc).Take(ClampCount(maxCount, _options.MaximumStoredComparisons)).ToArray();
    }

    public IReadOnlyList<TransportBatchOptimizationResult> GetOptimizations(int maxCount = 100)
    {
        lock (_sync)
            return _optimizations.OrderByDescending(x => x.GeneratedAtUtc).Take(ClampCount(maxCount, _options.MaximumStoredComparisons)).ToArray();
    }

    public IReadOnlyList<TransportCapacityBenchmarkReport> GetBenchmarks(int maxCount = 100)
    {
        lock (_sync)
            return _benchmarks.OrderByDescending(x => x.CompletedAtUtc).Take(ClampCount(maxCount, _options.MaximumStoredComparisons)).ToArray();
    }

    public IReadOnlyList<TransportFinalAcceptanceReport> GetAcceptanceReports(int maxCount = 100)
    {
        lock (_sync)
            return _acceptanceReports.OrderByDescending(x => x.GeneratedAtUtc).Take(ClampCount(maxCount, _options.MaximumStoredComparisons)).ToArray();
    }

    public TransportSimulationSummary GetSummary()
    {
        lock (_sync)
        {
            return new TransportSimulationSummary
            {
                LatestRun = _runs.OrderByDescending(x => x.CompletedAtUtc).FirstOrDefault(),
                LatestComparison = _comparisons.OrderByDescending(x => x.CompletedAtUtc).FirstOrDefault(),
                LatestBenchmark = _benchmarks.OrderByDescending(x => x.CompletedAtUtc).FirstOrDefault(),
                LatestAcceptance = _acceptanceReports.OrderByDescending(x => x.GeneratedAtUtc).FirstOrDefault(),
                RunCount = _runs.Count,
                ComparisonCount = _comparisons.Count,
                BenchmarkCount = _benchmarks.Count,
                AcceptanceReportCount = _acceptanceReports.Count
            };
        }
    }

    private TransportSimulationRun ExecuteCore(
        TransportSimulationScenario scenario,
        TransportSimulationPolicy policy,
        string initiatedBy,
        CancellationToken cancellationToken,
        bool drainAfterHorizon = false)
    {
        var started = DateTime.UtcNow;
        var vehicles = scenario.Vehicles
            .Select(x => new VehicleState(x))
            .ToArray();
        var stations = scenario.Stations
            .ToDictionary(x => x.StationId, x => new StationState(x), StringComparer.Ordinal);
        var resourceIntervals = new Dictionary<string, List<SimulationInterval>>(StringComparer.Ordinal);
        var pending = scenario.Tasks
            .OrderBy(x => x.ArrivalOffsetSeconds)
            .ThenBy(x => x.TaskId, StringComparer.Ordinal)
            .ToList();
        var results = new List<TransportSimulationTaskResult>(pending.Count);
        var now = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextArrival = pending.Min(x => x.ArrivalOffsetSeconds);
            if (!pending.Any(x => x.ArrivalOffsetSeconds <= now))
                now = Math.Max(now, nextArrival);
            var arrived = pending.Where(x => x.ArrivalOffsetSeconds <= now).ToArray();
            var destinationCounts = arrived
                .Where(x => !string.IsNullOrWhiteSpace(x.DestinationStationId))
                .GroupBy(x => x.DestinationStationId!, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var task = arrived
                .OrderByDescending(x => CalculatePriority(x, policy, now, destinationCounts.GetValueOrDefault(x.DestinationStationId ?? string.Empty)))
                .ThenBy(x => x.ArrivalOffsetSeconds)
                .ThenBy(x => x.TaskId, StringComparer.Ordinal)
                .First();
            var effectivePriority = CalculatePriority(
                task,
                policy,
                now,
                destinationCounts.GetValueOrDefault(task.DestinationStationId ?? string.Empty));
            var duration = Math.Max(1, task.EstimatedTravelSeconds + task.ServiceSeconds);
            var candidates = vehicles
                .Where(x => x.Definition.Online &&
                            x.Definition.BatteryPercent >= policy.MinimumBatteryPercent &&
                            (!task.RequiredVehicleKind.HasValue || task.RequiredVehicleKind.Value == x.Definition.Kind))
                .Select(x => new
                {
                    Vehicle = x,
                    ReadyAt = ShiftVehicleAvailability(
                        Math.Max(now, x.AvailableAtSeconds),
                        duration,
                        x.Definition.VehicleId,
                        scenario.Faults)
                })
                .OrderBy(x => x.ReadyAt)
                .ThenBy(x => x.Vehicle.Definition.VehicleId, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                results.Add(FailedTask(task, effectivePriority, now, "没有满足车型、电量和在线条件的仿真车辆"));
                pending.Remove(task);
                continue;
            }

            var selected = candidates[0];
            var dispatch = selected.ReadyAt;
            StationState? station = null;
            var stationSlot = -1;
            var waitBeforeStation = dispatch;
            if (!string.IsNullOrWhiteSpace(task.DestinationStationId))
            {
                if (!stations.TryGetValue(task.DestinationStationId, out station) || !station.Definition.Enabled)
                {
                    results.Add(FailedTask(task, effectivePriority, now, $"目标站点 {task.DestinationStationId} 未启用或不存在"));
                    pending.Remove(task);
                    continue;
                }
                stationSlot = station.GetEarliestSlotIndex();
                dispatch = Math.Max(dispatch, station.SlotAvailableAtSeconds[stationSlot]);
                duration += Math.Max(0, station.Definition.AdditionalServiceSeconds);
                dispatch = ShiftPastFaults(
                    dispatch,
                    duration,
                    scenario.Faults.Where(x =>
                        x.FaultType == TransportSimulationFaultType.StationBlocked &&
                        TargetMatches(x.TargetId, station.Definition.StationId)));
                if (dispatch > waitBeforeStation)
                    station.WaitingTaskCount++;
            }
            dispatch = ShiftPastFaults(
                dispatch,
                duration,
                scenario.Faults.Where(x =>
                    x.FaultType == TransportSimulationFaultType.TrafficResourceBlocked &&
                    task.ResourceIds.Any(resourceId => TargetMatches(x.TargetId, resourceId))));
            var latency = scenario.Faults
                .Where(x => x.FaultType == TransportSimulationFaultType.DriverLatency &&
                            IsWithin(dispatch, x) &&
                            (TargetMatches(x.TargetId, selected.Vehicle.Definition.VehicleId) || TargetMatches(x.TargetId, task.TaskId)))
                .Sum(x => Math.Max(0, x.AddedLatencySeconds));
            duration += latency;

            if (!drainAfterHorizon && dispatch > scenario.HorizonSeconds)
            {
                results.Add(FailedTask(task, effectivePriority, scenario.HorizonSeconds, "任务在仿真窗口内无法开始"));
                pending.Remove(task);
                continue;
            }
            var commandFailure = scenario.Faults.FirstOrDefault(x =>
                x.FaultType == TransportSimulationFaultType.CommandFailure &&
                IsWithin(dispatch, x) &&
                (TargetMatches(x.TargetId, selected.Vehicle.Definition.VehicleId) ||
                 TargetMatches(x.TargetId, task.TaskId) ||
                 x.TargetId == "*"));
            if (commandFailure is not null &&
                DeterministicProbability(scenario.Seed, task.TaskId, commandFailure.FaultId) < Math.Clamp(commandFailure.FailureProbability, 0, 1))
            {
                var releaseAt = drainAfterHorizon
                    ? dispatch + Math.Max(1, latency)
                    : Math.Min(scenario.HorizonSeconds, dispatch + Math.Max(1, latency));
                selected.Vehicle.AvailableAtSeconds = releaseAt;
                results.Add(new TransportSimulationTaskResult
                {
                    TaskId = task.TaskId,
                    VehicleId = selected.Vehicle.Definition.VehicleId,
                    Completed = false,
                    ArrivalOffsetSeconds = task.ArrivalOffsetSeconds,
                    DispatchOffsetSeconds = dispatch,
                    CompletionOffsetSeconds = releaseAt,
                    WaitingSeconds = Math.Max(0, dispatch - task.ArrivalOffsetSeconds),
                    CycleSeconds = Math.Max(0, releaseAt - task.ArrivalOffsetSeconds),
                    FailureReason = "仿真命令失败故障命中",
                    EffectivePriority = effectivePriority
                });
                pending.Remove(task);
                continue;
            }

            var completion = dispatch + duration;
            if (!drainAfterHorizon && completion > scenario.HorizonSeconds)
            {
                selected.Vehicle.AvailableAtSeconds = scenario.HorizonSeconds;
                results.Add(new TransportSimulationTaskResult
                {
                    TaskId = task.TaskId,
                    VehicleId = selected.Vehicle.Definition.VehicleId,
                    Completed = false,
                    ArrivalOffsetSeconds = task.ArrivalOffsetSeconds,
                    DispatchOffsetSeconds = dispatch,
                    CompletionOffsetSeconds = scenario.HorizonSeconds,
                    WaitingSeconds = Math.Max(0, dispatch - task.ArrivalOffsetSeconds),
                    CycleSeconds = Math.Max(0, scenario.HorizonSeconds - task.ArrivalOffsetSeconds),
                    FailureReason = "任务执行超出仿真窗口",
                    EffectivePriority = effectivePriority
                });
                pending.Remove(task);
                continue;
            }

            selected.Vehicle.AvailableAtSeconds = completion;
            selected.Vehicle.BusySeconds += duration;
            if (station is not null && stationSlot >= 0)
            {
                station.SlotAvailableAtSeconds[stationSlot] = completion;
                station.BusySeconds += duration;
                station.Intervals.Add(new SimulationInterval(dispatch, completion));
            }
            foreach (var resourceId in task.ResourceIds.Distinct(StringComparer.Ordinal))
            {
                if (!resourceIntervals.TryGetValue(resourceId, out var intervals))
                {
                    intervals = new List<SimulationInterval>();
                    resourceIntervals[resourceId] = intervals;
                }
                intervals.Add(new SimulationInterval(dispatch, completion));
            }
            results.Add(new TransportSimulationTaskResult
            {
                TaskId = task.TaskId,
                VehicleId = selected.Vehicle.Definition.VehicleId,
                Completed = true,
                DeadlineMissed = task.DeadlineOffsetSeconds.HasValue && completion > task.DeadlineOffsetSeconds.Value,
                ArrivalOffsetSeconds = task.ArrivalOffsetSeconds,
                DispatchOffsetSeconds = dispatch,
                CompletionOffsetSeconds = completion,
                WaitingSeconds = Math.Max(0, dispatch - task.ArrivalOffsetSeconds),
                CycleSeconds = Math.Max(0, completion - task.ArrivalOffsetSeconds),
                EffectivePriority = effectivePriority
            });
            pending.Remove(task);
        }

        var resources = BuildResourceMetrics(stations, resourceIntervals, scenario.HorizonSeconds);
        var forecast = BuildForecast(results, scenario.HorizonSeconds, scenario.Vehicles.Count);
        var metrics = BuildMetrics(results, vehicles, resources, scenario.HorizonSeconds);
        return new TransportSimulationRun
        {
            ScenarioId = scenario.ScenarioId,
            ScenarioName = scenario.Name,
            PolicyId = policy.PolicyId,
            PolicyName = policy.Name,
            InitiatedBy = initiatedBy.Trim(),
            Seed = scenario.Seed,
            Metrics = metrics,
            Tasks = results.OrderBy(x => x.DispatchOffsetSeconds).ThenBy(x => x.TaskId, StringComparer.Ordinal).ToArray(),
            Resources = resources,
            CongestionForecast = forecast,
            StartedAtUtc = started,
            CompletedAtUtc = DateTime.UtcNow
        };
    }

    private TransportSimulationTask ConvertTask(
        TransportProductionDispatchRequest request,
        DateTime baseTime,
        int? travelSeconds = null,
        int? serviceSeconds = null)
    {
        var allowedKinds = request.Request.AllowedVehicleKinds;
        TransportVehicleKind? requiredKind = allowedKinds is { Count: 1 } ? allowedKinds.First() : null;
        return new TransportSimulationTask
        {
            TaskId = request.Request.RequestId,
            SourceNodeId = request.Request.SourceNodeId,
            DestinationNodeId = request.Request.DestinationNodeId,
            DestinationStationId = request.DestinationStationId,
            ResourceIds = ResolveResourceIds(request.Request.SourceNodeId, request.Request.DestinationNodeId),
            RequiredVehicleKind = requiredKind,
            Priority = request.Request.Priority,
            ProductionOrderPriority = request.ProductionOrderPriority,
            IsRecoveryTask = request.IsRecoveryTask,
            ArrivalOffsetSeconds = Math.Max(0, (int)Math.Round((request.EnqueuedAtUtc - baseTime).TotalSeconds)),
            DeadlineOffsetSeconds = request.DeadlineAtUtc.HasValue
                ? Math.Max(0, (int)Math.Round((request.DeadlineAtUtc.Value - baseTime).TotalSeconds))
                : null,
            EstimatedTravelSeconds = Math.Max(1, travelSeconds ?? _options.DefaultTravelSeconds),
            ServiceSeconds = Math.Max(0, serviceSeconds ?? _options.DefaultServiceSeconds)
        };
    }

    private IReadOnlyList<TransportSimulationVehicle> BuildVehicleSnapshot() =>
        _vehicles.GetAll()
            .OrderBy(x => x.VehicleId, StringComparer.Ordinal)
            .Select(x => new TransportSimulationVehicle
            {
                VehicleId = x.VehicleId,
                Kind = x.Kind,
                InitialAvailableOffsetSeconds = x.State == TransportVehicleOperatingState.Idle ? 0 : _options.DefaultServiceSeconds,
                BatteryPercent = x.BatteryPercent,
                Online = x.IsOnline && x.State is not (TransportVehicleOperatingState.Faulted or TransportVehicleOperatingState.Maintenance)
            })
            .ToArray();

    private IReadOnlyList<TransportSimulationStation> BuildStationSnapshot() =>
        _stations.GetAll()
            .OrderBy(x => x.StationId, StringComparer.Ordinal)
            .Select(x => new TransportSimulationStation
            {
                StationId = x.StationId,
                Capacity = Math.Max(1, x.Capacity),
                Enabled = x.Enabled
            })
            .ToArray();

    private IReadOnlyList<string> ResolveResourceIds(string sourceNodeId, string destinationNodeId) =>
        _singleTrack.GetSnapshots()
            .Where(x => x.Definition.Enabled &&
                        (x.Definition.OrderedNodeIds.Contains(sourceNodeId, StringComparer.Ordinal) ||
                         x.Definition.OrderedNodeIds.Contains(destinationNodeId, StringComparer.Ordinal)))
            .Select(x => x.Definition.TrafficResourceId ?? x.Definition.SectionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private TransportSimulationScenario BuildStressScenario(
        TransportCapacityBenchmarkRequest request,
        int durationMinutes,
        int vehicleCount,
        int taskRate,
        int repetition)
    {
        var horizon = durationMinutes * 60;
        var taskCount = Math.Max(1, (int)Math.Round(taskRate * durationMinutes / 60d));
        var random = new Random(unchecked(request.Seed + taskRate * 104729 + repetition * 31));
        var tasks = Enumerable.Range(0, taskCount)
            .Select(index =>
            {
                var arrival = taskCount == 1 ? 0 : (int)Math.Round(index * (horizon - 1d) / taskCount);
                var arrivalOffset = Math.Clamp(arrival + random.Next(-2, 3), 0, horizon - 1);
                return new TransportSimulationTask
                {
                    TaskId = $"STRESS-{vehicleCount}-{taskRate}-{repetition}-{index:00000}",
                    SourceNodeId = $"SRC-{index % 4 + 1}",
                    DestinationNodeId = "DST-01",
                    DestinationStationId = "STRESS-STATION",
                    RequiredVehicleKind = request.VehicleKind,
                    Priority = random.Next(0, 20),
                    ProductionOrderPriority = random.Next(0, 30),
                    ArrivalOffsetSeconds = arrivalOffset,
                    DeadlineOffsetSeconds = arrivalOffset + 300,
                    EstimatedTravelSeconds = 20 + random.Next(0, 21),
                    ServiceSeconds = 5 + random.Next(0, 6),
                    ResourceIds = new[] { "STRESS-TRACK" }
                };
            })
            .OrderBy(x => x.ArrivalOffsetSeconds)
            .ToArray();
        return new TransportSimulationScenario
        {
            Name = $"{request.Name}-{vehicleCount}v-{taskRate}tph-r{repetition}",
            Description = "确定性容量压力场景",
            Source = TransportSimulationSource.CapacityStress,
            HorizonSeconds = horizon,
            Seed = unchecked(request.Seed + repetition),
            Tasks = tasks,
            Vehicles = Enumerable.Range(1, vehicleCount).Select(index => new TransportSimulationVehicle
            {
                VehicleId = $"SIM-{request.VehicleKind}-{index:00}",
                Kind = request.VehicleKind,
                Online = true,
                BatteryPercent = 100
            }).ToArray(),
            Stations = new[]
            {
                new TransportSimulationStation
                {
                    StationId = "STRESS-STATION",
                    Capacity = Math.Max(1, vehicleCount / 2)
                }
            }
        };
    }

    private static int CalculatePriority(
        TransportSimulationTask task,
        TransportSimulationPolicy policy,
        int nowSeconds,
        int sameDestinationQueuedCount)
    {
        var waitedSeconds = Math.Max(0, nowSeconds - task.ArrivalOffsetSeconds);
        var aging = (int)Math.Floor(waitedSeconds / 60d) * policy.AgingPointsPerMinute;
        var deadline = 0;
        if (task.DeadlineOffsetSeconds.HasValue)
        {
            var remaining = task.DeadlineOffsetSeconds.Value - nowSeconds;
            deadline = remaining <= 0
                ? policy.DeadlineUrgencyPoints * 2
                : remaining <= 600
                    ? policy.DeadlineUrgencyPoints
                    : 0;
        }
        var recovery = task.IsRecoveryTask ? policy.RecoveryTaskBoost : 0;
        var congestion = Math.Max(0, sameDestinationQueuedCount - 1) * policy.CongestionPenaltyPerQueuedTask;
        var score = task.Priority + task.ProductionOrderPriority + aging + deadline + recovery - congestion;
        score += policy.Strategy switch
        {
            TransportSimulationStrategyKind.AgingFirst => waitedSeconds / 10,
            TransportSimulationStrategyKind.DeadlineFirst => deadline * 2,
            TransportSimulationStrategyKind.CongestionAware => -congestion,
            TransportSimulationStrategyKind.BalancedBatch when policy.FavorSameDestinationBatch => sameDestinationQueuedCount * 5,
            _ => 0
        };
        return score;
    }

    private static int ShiftVehicleAvailability(
        int readyAt,
        int duration,
        string vehicleId,
        IReadOnlyList<TransportSimulationFault> faults) =>
        ShiftPastFaults(
            readyAt,
            duration,
            faults.Where(x =>
                x.FaultType is TransportSimulationFaultType.VehicleOffline or TransportSimulationFaultType.HeartbeatTimeout &&
                TargetMatches(x.TargetId, vehicleId)));

    private static int ShiftPastFaults(
        int start,
        int duration,
        IEnumerable<TransportSimulationFault> faults)
    {
        var result = start;
        var ordered = faults.OrderBy(x => x.StartOffsetSeconds).ThenBy(x => x.EndOffsetSeconds).ToArray();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var fault in ordered)
            {
                if (IntervalsOverlap(result, result + duration, fault.StartOffsetSeconds, fault.EndOffsetSeconds))
                {
                    result = Math.Max(result, fault.EndOffsetSeconds);
                    changed = true;
                }
            }
        }
        return result;
    }

    private static bool IntervalsOverlap(int startA, int endA, int startB, int endB) =>
        startA < endB && startB < endA;

    private static bool IsWithin(int offsetSeconds, TransportSimulationFault fault) =>
        offsetSeconds >= fault.StartOffsetSeconds && offsetSeconds < fault.EndOffsetSeconds;

    private static bool TargetMatches(string target, string actual) =>
        string.Equals(target, "*", StringComparison.Ordinal) ||
        string.Equals(target, actual, StringComparison.Ordinal);

    private static double DeterministicProbability(int seed, string taskId, string faultId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{taskId}:{faultId}"));
        var value = BitConverter.ToUInt64(bytes, 0);
        return value / (double)ulong.MaxValue;
    }

    private IReadOnlyList<TransportSimulationResourceMetric> BuildResourceMetrics(
        IReadOnlyDictionary<string, StationState> stations,
        IReadOnlyDictionary<string, List<SimulationInterval>> resources,
        int horizonSeconds)
    {
        var result = new List<TransportSimulationResourceMetric>();
        foreach (var station in stations.Values.OrderBy(x => x.Definition.StationId, StringComparer.Ordinal))
        {
            result.Add(new TransportSimulationResourceMetric
            {
                ResourceId = station.Definition.StationId,
                ResourceType = "Station",
                UtilizationPercent = Percent(station.BusySeconds, Math.Max(1, station.Definition.Capacity * horizonSeconds)),
                MaximumConcurrentCount = MaximumConcurrency(station.Intervals),
                WaitingTaskCount = station.WaitingTaskCount
            });
        }
        foreach (var resource in resources.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            result.Add(new TransportSimulationResourceMetric
            {
                ResourceId = resource.Key,
                ResourceType = "TrafficResource",
                UtilizationPercent = Percent(resource.Value.Sum(x => x.End - x.Start), Math.Max(1, horizonSeconds)),
                MaximumConcurrentCount = MaximumConcurrency(resource.Value),
                WaitingTaskCount = 0
            });
        }
        return result;
    }

    private IReadOnlyList<TransportCongestionForecastPoint> BuildForecast(
        IReadOnlyList<TransportSimulationTaskResult> results,
        int horizonSeconds,
        int vehicleCount)
    {
        var bucket = Math.Clamp(_options.ForecastBucketSeconds, 10, 3600);
        var points = new List<TransportCongestionForecastPoint>();
        for (var offset = 0; offset <= horizonSeconds; offset += bucket)
        {
            var queue = results.Count(x => x.ArrivalOffsetSeconds <= offset && x.DispatchOffsetSeconds > offset);
            var active = results.Count(x => x.VehicleId is not null && x.DispatchOffsetSeconds <= offset && x.CompletionOffsetSeconds > offset);
            var utilization = vehicleCount == 0 ? 0 : Math.Min(100, active * 100d / vehicleCount);
            var level = queue >= Math.Max(5, vehicleCount * 3) || utilization >= 90
                ? "Heavy"
                : queue >= Math.Max(3, vehicleCount * 2) || utilization >= 75
                    ? "Moderate"
                    : queue > 0 || utilization >= 50
                        ? "Light"
                        : "Clear";
            points.Add(new TransportCongestionForecastPoint
            {
                OffsetSeconds = offset,
                QueueLength = queue,
                ActiveTaskCount = active,
                FleetUtilizationPercent = Math.Round(utilization, 2),
                CongestionLevel = level
            });
        }
        return points;
    }

    private static TransportSimulationMetrics BuildMetrics(
        IReadOnlyList<TransportSimulationTaskResult> results,
        IReadOnlyList<VehicleState> vehicles,
        IReadOnlyList<TransportSimulationResourceMetric> resources,
        int horizonSeconds)
    {
        var completed = results.Where(x => x.Completed).ToArray();
        var waits = completed.Select(x => (double)x.WaitingSeconds).OrderBy(x => x).ToArray();
        var throughput = completed.Length / Math.Max(1d / 3600d, horizonSeconds / 3600d);
        var deadlineMissRate = completed.Length == 0
            ? 0
            : completed.Count(x => x.DeadlineMissed) * 100d / completed.Length;
        var fleetUtilization = Percent(vehicles.Sum(x => x.BusySeconds), Math.Max(1, vehicles.Count * horizonSeconds));
        var maxStation = resources.Where(x => x.ResourceType == "Station").Select(x => x.UtilizationPercent).DefaultIfEmpty(0).Max();
        var maximumQueue = MaximumQueueLength(results);
        var p95 = Percentile(waits, 0.95);
        var averageWait = waits.Length == 0 ? 0 : waits.Average();
        var averageCycle = completed.Length == 0 ? 0 : completed.Average(x => x.CycleSeconds);
        var failed = results.Count - completed.Length;
        var objective = throughput * 10 -
                        averageWait * 0.1 -
                        p95 * 0.2 -
                        deadlineMissRate * 5 -
                        failed * 20 -
                        maximumQueue * 2;
        return new TransportSimulationMetrics
        {
            TotalTaskCount = results.Count,
            CompletedTaskCount = completed.Length,
            FailedTaskCount = failed,
            ThroughputPerHour = Math.Round(throughput, 2),
            AverageWaitingSeconds = Math.Round(averageWait, 2),
            P95WaitingSeconds = Math.Round(p95, 2),
            MaximumWaitingSeconds = waits.Length == 0 ? 0 : (int)waits.Max(),
            AverageCycleSeconds = Math.Round(averageCycle, 2),
            DeadlineMissRatePercent = Math.Round(deadlineMissRate, 2),
            MaximumQueueLength = maximumQueue,
            FleetUtilizationPercent = Math.Round(fleetUtilization, 2),
            MaximumStationUtilizationPercent = Math.Round(maxStation, 2),
            BlockedByFaultCount = results.Count(x => x.FailureReason?.Contains("故障", StringComparison.Ordinal) == true ||
                                                     x.FailureReason?.Contains("窗口", StringComparison.Ordinal) == true),
            ObjectiveScore = Math.Round(objective, 2)
        };
    }

    private static CapacityBenchmarkSample BuildCapacityBenchmarkSample(
        TransportSimulationRun run,
        int cutoffSeconds,
        int vehicleCount)
    {
        var arrived = run.Tasks
            .Where(x => x.ArrivalOffsetSeconds < cutoffSeconds)
            .ToArray();
        var successful = arrived.Where(x => x.Completed).ToArray();
        var completedByCutoff = successful.Count(x => x.CompletionOffsetSeconds <= cutoffSeconds);
        var outstandingAtCutoff = successful.Length - completedByCutoff;
        var failed = arrived.Length - successful.Length;
        var waits = successful
            .Select(x => (double)x.WaitingSeconds)
            .OrderBy(x => x)
            .ToArray();
        var throughput = completedByCutoff /
                         Math.Max(1d / 3600d, cutoffSeconds / 3600d);
        var deadlineMissRate = successful.Length == 0
            ? 0
            : successful.Count(x => x.DeadlineMissed) * 100d / successful.Length;
        var fleetBusySeconds = arrived
            .Where(x => x.VehicleId is not null)
            .Sum(x => BusySecondsWithinHorizon(
                x.DispatchOffsetSeconds,
                x.CompletionOffsetSeconds,
                cutoffSeconds));
        var fleetUtilization = Percent(
            fleetBusySeconds,
            Math.Max(1L, (long)vehicleCount * cutoffSeconds));
        return new CapacityBenchmarkSample(
            arrived.Length,
            completedByCutoff,
            outstandingAtCutoff,
            failed,
            Math.Round(throughput, 2),
            Math.Round(Percentile(waits, 0.95), 2),
            Math.Round(deadlineMissRate, 2),
            fleetUtilization);
    }

    private static int MaximumQueueLength(IReadOnlyList<TransportSimulationTaskResult> results)
    {
        var events = results
            .SelectMany(x => new[]
            {
                (Time: x.ArrivalOffsetSeconds, Delta: 1),
                (Time: Math.Max(x.ArrivalOffsetSeconds, x.DispatchOffsetSeconds), Delta: -1)
            })
            .OrderBy(x => x.Time)
            .ThenByDescending(x => x.Delta)
            .ToArray();
        var current = 0;
        var maximum = 0;
        foreach (var item in events)
        {
            current += item.Delta;
            maximum = Math.Max(maximum, current);
        }
        return maximum;
    }

    private static int MaximumConcurrency(IReadOnlyList<SimulationInterval> intervals)
    {
        var events = intervals
            .SelectMany(x => new[] { (Time: x.Start, Delta: 1), (Time: x.End, Delta: -1) })
            .OrderBy(x => x.Time)
            .ThenBy(x => x.Delta)
            .ToArray();
        var current = 0;
        var maximum = 0;
        foreach (var item in events)
        {
            current += item.Delta;
            maximum = Math.Max(maximum, current);
        }
        return maximum;
    }

    private static long BusySecondsWithinHorizon(
        int startSeconds,
        int endSeconds,
        int horizonSeconds)
    {
        var start = Math.Clamp(startSeconds, 0, horizonSeconds);
        var end = Math.Clamp(endSeconds, 0, horizonSeconds);
        return Math.Max(0, end - start);
    }

    private static double Percent(double numerator, double denominator) =>
        Math.Round(Math.Clamp(numerator * 100d / Math.Max(1, denominator), 0, 100), 2);

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static TransportSimulationTaskResult FailedTask(
        TransportSimulationTask task,
        int priority,
        int offset,
        string reason) => new()
    {
        TaskId = task.TaskId,
        Completed = false,
        ArrivalOffsetSeconds = task.ArrivalOffsetSeconds,
        DispatchOffsetSeconds = Math.Max(task.ArrivalOffsetSeconds, offset),
        CompletionOffsetSeconds = Math.Max(task.ArrivalOffsetSeconds, offset),
        WaitingSeconds = Math.Max(0, offset - task.ArrivalOffsetSeconds),
        CycleSeconds = Math.Max(0, offset - task.ArrivalOffsetSeconds),
        FailureReason = reason,
        EffectivePriority = priority
    };

    private static TransportSimulationPolicy CreatePolicy(
        string name,
        TransportSimulationStrategyKind strategy,
        TransportProductionTuningOptions tuning,
        bool batch) => new()
    {
        Name = name,
        Strategy = strategy,
        AgingPointsPerMinute = tuning.AgingPointsPerMinute,
        DeadlineUrgencyPoints = tuning.DeadlineUrgencyPoints,
        RecoveryTaskBoost = tuning.RecoveryTaskBoost,
        CongestionPenaltyPerQueuedTask = tuning.CongestionPenaltyPerQueuedTask,
        MaximumBatchSize = tuning.MaximumDispatchPerCycle,
        FavorSameDestinationBatch = batch
    };

    private static string BuildComparisonRecommendation(
        TransportStrategyComparisonItem winner,
        TransportStrategyComparisonItem baseline)
    {
        var throughputDelta = winner.Metrics.ThroughputPerHour - baseline.Metrics.ThroughputPerHour;
        var waitDelta = winner.Metrics.P95WaitingSeconds - baseline.Metrics.P95WaitingSeconds;
        return $"推荐 {winner.PolicyName}；相对基准吞吐变化 {throughputDelta:+0.##;-0.##;0}/h，P95 等待变化 {waitDelta:+0.##;-0.##;0}s。推荐结果仅用于离线评审。";
    }

    private static TransportAcceptanceCheck MinimumCheck(
        string name,
        double actual,
        double required,
        string unit) => new()
    {
        Name = name,
        Passed = actual >= required,
        ActualValue = Math.Round(actual, 2),
        RequiredValue = required,
        Comparison = ">=",
        Message = $"{actual:F2}{unit}，要求不低于 {required:F2}{unit}"
    };

    private static TransportAcceptanceCheck MaximumCheck(
        string name,
        double actual,
        double required,
        string unit) => new()
    {
        Name = name,
        Passed = actual <= required,
        ActualValue = Math.Round(actual, 2),
        RequiredValue = required,
        Comparison = "<=",
        Message = $"{actual:F2}{unit}，要求不高于 {required:F2}{unit}"
    };

    private async Task<IReadOnlyList<T>> LoadRecordsAsync<T>(
        TransportJournalCategory category,
        int maximum,
        CancellationToken cancellationToken)
    {
        var records = await _journal.QueryAsync(
            category,
            Math.Clamp(maximum, 1, 5000),
            cancellationToken).ConfigureAwait(false);
        return records
            .Select(x => Deserialize<T>(x.PayloadJson))
            .Where(x => x is not null)
            .Cast<T>()
            .ToArray();
    }

    private Task PersistAsync<T>(
        TransportJournalCategory category,
        string recordId,
        T payload,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken) =>
        _journal.UpsertAsync(new TransportJournalRecord
        {
            Category = category,
            RecordId = recordId,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAtUtc = occurredAtUtc
        }, cancellationToken);

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return default; }
    }

    private void ValidateScenario(TransportSimulationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ValidateName(scenario.Name);
        if (scenario.HorizonSeconds <= 0)
            throw new ArgumentException("HorizonSeconds 必须大于 0", nameof(scenario));
        if (scenario.Tasks.Count == 0)
            throw new ArgumentException("仿真场景至少包含一个任务", nameof(scenario));
        if (scenario.Tasks.Count > _options.MaximumScenarioTasks)
            throw new ArgumentException($"仿真任务数量不能超过 {_options.MaximumScenarioTasks}", nameof(scenario));
        if (scenario.Vehicles.Count == 0)
            throw new ArgumentException("仿真场景至少包含一台车辆", nameof(scenario));
        ValidateUnique(scenario.Tasks.Select(x => x.TaskId), "任务号");
        ValidateUnique(scenario.Vehicles.Select(x => x.VehicleId), "车辆号");
        ValidateUnique(scenario.Stations.Select(x => x.StationId), "站点号");
        foreach (var task in scenario.Tasks)
        {
            if (task.ArrivalOffsetSeconds < 0 || task.EstimatedTravelSeconds <= 0 || task.ServiceSeconds < 0)
                throw new ArgumentException($"任务 {task.TaskId} 的时间参数非法", nameof(scenario));
        }
        foreach (var station in scenario.Stations)
        {
            if (station.Capacity <= 0)
                throw new ArgumentException($"站点 {station.StationId} Capacity 必须大于 0", nameof(scenario));
        }
        foreach (var fault in scenario.Faults)
        {
            if (fault.EndOffsetSeconds <= fault.StartOffsetSeconds)
                throw new ArgumentException($"故障 {fault.FaultId} 的结束时间必须晚于开始时间", nameof(scenario));
        }
    }

    private static void ValidatePolicy(TransportSimulationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateName(policy.Name);
        if (policy.MaximumBatchSize <= 0 || policy.MinimumBatteryPercent is < 0 or > 100)
            throw new ArgumentException("策略批量大小或最低电量参数非法", nameof(policy));
    }

    private static void ValidateUnique(IEnumerable<string> values, string name)
    {
        var all = values.ToArray();
        if (all.Any(string.IsNullOrWhiteSpace) || all.Distinct(StringComparer.Ordinal).Count() != all.Length)
            throw new ArgumentException($"{name}不能为空且不能重复");
    }

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("名称不能为空");
    }

    private static void ValidateActor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("执行人不能为空", nameof(value));
    }

    private static int ClampCount(int requested, int maximum) =>
        Math.Clamp(requested, 1, Math.Max(1, maximum));

    private static void ReplaceUnsafe<T>(List<T> target, IEnumerable<T> values, int capacity)
    {
        target.Clear();
        target.AddRange(values);
        TrimUnsafe(target, capacity);
    }

    private static void TrimUnsafe<T>(List<T> items, int capacity)
    {
        var excess = items.Count - Math.Max(1, capacity);
        if (excess > 0)
            items.RemoveRange(0, excess);
    }

    private sealed record CapacityBenchmarkSample(
        int ArrivedTaskCount,
        int CompletedTaskCount,
        int OutstandingTaskCount,
        int FailedTaskCount,
        double ThroughputPerHour,
        double P95WaitingSeconds,
        double DeadlineMissRatePercent,
        double FleetUtilizationPercent);

    private sealed class VehicleState
    {
        public VehicleState(TransportSimulationVehicle definition)
        {
            Definition = definition;
            AvailableAtSeconds = Math.Max(0, definition.InitialAvailableOffsetSeconds);
        }

        public TransportSimulationVehicle Definition { get; }
        public int AvailableAtSeconds { get; set; }
        public int BusySeconds { get; set; }
    }

    private sealed class StationState
    {
        public StationState(TransportSimulationStation definition)
        {
            Definition = definition;
            SlotAvailableAtSeconds = new int[Math.Max(1, definition.Capacity)];
        }

        public TransportSimulationStation Definition { get; }
        public int[] SlotAvailableAtSeconds { get; }
        public int BusySeconds { get; set; }
        public int WaitingTaskCount { get; set; }
        public List<SimulationInterval> Intervals { get; } = new();
        public int GetEarliestSlotIndex()
        {
            var index = 0;
            for (var current = 1; current < SlotAvailableAtSeconds.Length; current++)
            {
                if (SlotAvailableAtSeconds[current] < SlotAvailableAtSeconds[index])
                    index = current;
            }
            return index;
        }
    }

    private sealed record SimulationInterval(int Start, int End);
}
