namespace Wcs.Core.TransportScheduling;

/// <summary>
/// 对离线仿真入口增加资源上限和串行保护。
/// 该门面不改变仿真结果，只阻止可能占满生产 Host CPU/内存的危险请求。
/// </summary>
public sealed class SafeTransportSimulationService : ITransportSimulationService
{
    private const int MaximumVehiclesPerScenario = 1000;
    private const int MaximumStationsPerScenario = 10000;
    private const int MaximumFaultsPerScenario = 10000;
    private const int MaximumCapacityVehicles = 200;
    private const int MaximumCapacityTaskRatePerHour = 10000;
    private const int MaximumCapacityGridPoints = 200;
    private const long MaximumCapacityEstimatedTasks = 2_000_000;
    private static readonly TimeSpan MaximumHistoricalWindow = TimeSpan.FromDays(30);

    private readonly TransportSimulationService _inner;
    private readonly ITransportTelemetryService _telemetry;
    private readonly TransportSimulationOptions _options;
    private readonly SemaphoreSlim _capacityGate = new(1, 1);

    public SafeTransportSimulationService(
        TransportSimulationService inner,
        ITransportTelemetryService telemetry,
        TransportSimulationOptions options)
    {
        _inner = inner;
        _telemetry = telemetry;
        _options = options;
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(cancellationToken);

    public Task<TransportSimulationScenario> BuildCurrentScenarioAsync(
        string name,
        int horizonSeconds = 3600,
        int maximumTasks = 1000,
        CancellationToken cancellationToken = default) =>
        _inner.BuildCurrentScenarioAsync(
            name,
            horizonSeconds,
            Math.Clamp(maximumTasks, 1, Math.Max(1, _options.MaximumScenarioTasks)),
            cancellationToken);

    public Task<TransportSimulationScenario> BuildHistoricalScenarioAsync(
        TransportHistoricalReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ToUtc < request.FromUtc)
            throw new ArgumentException("ToUtc 不能早于 FromUtc", nameof(request));
        if (request.ToUtc - request.FromUtc > MaximumHistoricalWindow)
            throw new ArgumentException("单次历史回放窗口不能超过 30 天", nameof(request));
        return _inner.BuildHistoricalScenarioAsync(
            request with
            {
                MaximumTasks = Math.Clamp(
                    request.MaximumTasks,
                    1,
                    Math.Max(1, _options.MaximumScenarioTasks))
            },
            cancellationToken);
    }

    public Task<TransportSimulationRun> RunAsync(
        TransportSimulationScenario scenario,
        TransportSimulationPolicy policy,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateScenarioEnvelope(scenario);
        return _inner.RunAsync(scenario, policy, initiatedBy, cancellationToken);
    }

    public Task<TransportStrategyComparisonReport> CompareAsync(
        TransportSimulationScenario scenario,
        IReadOnlyList<TransportSimulationPolicy> policies,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateScenarioEnvelope(scenario);
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Select(x => x.PolicyId).Distinct(StringComparer.Ordinal).Count() != policies.Count)
            throw new ArgumentException("策略对比中的 PolicyId 不能重复", nameof(policies));
        return _inner.CompareAsync(scenario, policies, initiatedBy, cancellationToken);
    }

    public Task<TransportBatchOptimizationResult> OptimizeBatchAsync(
        TransportSimulationScenario scenario,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateScenarioEnvelope(scenario);
        return _inner.OptimizeBatchAsync(scenario, initiatedBy, cancellationToken);
    }

    public async Task<TransportCapacityBenchmarkReport> RunCapacityBenchmarkAsync(
        TransportCapacityBenchmarkRequest request,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vehicles = request.VehicleCounts
            .Distinct()
            .Where(x => x > 0)
            .ToArray();
        var rates = request.TaskRatesPerHour
            .Distinct()
            .Where(x => x > 0)
            .ToArray();
        if (vehicles.Length == 0 || rates.Length == 0)
            throw new ArgumentException("车辆数量和任务率至少各包含一个正整数", nameof(request));
        if (vehicles.Any(x => x > MaximumCapacityVehicles))
            throw new ArgumentException($"单个容量点车辆数不能超过 {MaximumCapacityVehicles}", nameof(request));
        if (rates.Any(x => x > MaximumCapacityTaskRatePerHour))
            throw new ArgumentException($"单个容量点任务率不能超过 {MaximumCapacityTaskRatePerHour}/h", nameof(request));
        var gridPoints = (long)vehicles.Length * rates.Length;
        if (gridPoints > MaximumCapacityGridPoints)
            throw new ArgumentException($"容量网格不能超过 {MaximumCapacityGridPoints} 个组合", nameof(request));

        var durationMinutes = Math.Clamp(request.DurationMinutes, 5, 24 * 60);
        var repetitions = Math.Clamp(request.Repetitions, 1, 10);
        var maximumRate = rates.Max();
        var estimatedTasks = gridPoints * repetitions *
            Math.Max(1L, (long)Math.Ceiling(maximumRate * durationMinutes / 60d));
        if (estimatedTasks > MaximumCapacityEstimatedTasks)
            throw new ArgumentException(
                $"容量请求预计生成 {estimatedTasks:N0} 个任务，超过安全上限 {MaximumCapacityEstimatedTasks:N0}",
                nameof(request));

        if (!await _capacityGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("已有容量压力仿真正在运行，请等待该任务完成");
        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.CapacityBenchmark,
            "transport.simulation.capacity.guard",
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["capacity.grid.points"] = gridPoints.ToString(),
                ["capacity.estimated.tasks"] = estimatedTasks.ToString()
            });
        try
        {
            var result = await _inner.RunCapacityBenchmarkAsync(
                request with
                {
                    VehicleCounts = vehicles,
                    TaskRatesPerHour = rates,
                    DurationMinutes = durationMinutes,
                    Repetitions = repetitions
                },
                initiatedBy,
                cancellationToken).ConfigureAwait(false);
            operation.Complete(true, result.Conclusion);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            throw;
        }
        finally
        {
            _capacityGate.Release();
        }
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
        ValidateAcceptanceCriteria(criteria);
        if (!string.IsNullOrWhiteSpace(comparisonId) &&
            !_inner.GetComparisons(1000).Any(x => string.Equals(x.ComparisonId, comparisonId, StringComparison.Ordinal)))
        {
            throw new KeyNotFoundException($"策略对比 {comparisonId} 不存在");
        }
        if (!string.IsNullOrWhiteSpace(benchmarkId) &&
            !_inner.GetBenchmarks(1000).Any(x => string.Equals(x.BenchmarkId, benchmarkId, StringComparison.Ordinal)))
        {
            throw new KeyNotFoundException($"容量基线 {benchmarkId} 不存在");
        }

        using var operation = _telemetry.StartOperation(
            TransportTraceOperationKind.FinalAcceptance,
            "transport.simulation.acceptance",
            tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["simulation.run.id"] = simulationRunId,
                ["comparison.id"] = comparisonId ?? string.Empty,
                ["benchmark.id"] = benchmarkId ?? string.Empty
            });
        try
        {
            var result = await _inner.GenerateAcceptanceReportAsync(
                name,
                simulationRunId,
                criteria,
                initiatedBy,
                comparisonId,
                benchmarkId,
                cancellationToken).ConfigureAwait(false);
            operation.Complete(result.State != TransportAcceptanceState.Failed, result.Conclusion);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            operation.Complete(false, ex.Message);
            throw;
        }
    }

    public IReadOnlyList<TransportSimulationRun> GetRuns(int maxCount = 100) =>
        _inner.GetRuns(maxCount);

    public IReadOnlyList<TransportStrategyComparisonReport> GetComparisons(int maxCount = 100) =>
        _inner.GetComparisons(maxCount);

    public IReadOnlyList<TransportBatchOptimizationResult> GetOptimizations(int maxCount = 100) =>
        _inner.GetOptimizations(maxCount);

    public IReadOnlyList<TransportCapacityBenchmarkReport> GetBenchmarks(int maxCount = 100) =>
        _inner.GetBenchmarks(maxCount);

    public IReadOnlyList<TransportFinalAcceptanceReport> GetAcceptanceReports(int maxCount = 100) =>
        _inner.GetAcceptanceReports(maxCount);

    public TransportSimulationSummary GetSummary() => _inner.GetSummary();

    private void ValidateScenarioEnvelope(TransportSimulationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.Tasks.Count > _options.MaximumScenarioTasks)
            throw new ArgumentException($"仿真任务数量不能超过 {_options.MaximumScenarioTasks}", nameof(scenario));
        if (scenario.Vehicles.Count > MaximumVehiclesPerScenario)
            throw new ArgumentException($"单场景车辆数不能超过 {MaximumVehiclesPerScenario}", nameof(scenario));
        if (scenario.Stations.Count > MaximumStationsPerScenario)
            throw new ArgumentException($"单场景站点数不能超过 {MaximumStationsPerScenario}", nameof(scenario));
        if (scenario.Faults.Count > MaximumFaultsPerScenario)
            throw new ArgumentException($"单场景故障数不能超过 {MaximumFaultsPerScenario}", nameof(scenario));
    }

    private static void ValidateAcceptanceCriteria(TransportAcceptanceCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.MinimumThroughputPerHour < 0 ||
            criteria.MaximumP95WaitingSeconds < 0 ||
            criteria.MaximumQueueLength < 0)
        {
            throw new ArgumentException("吞吐、等待和队列验收门槛不能为负数", nameof(criteria));
        }
        if (criteria.MaximumDeadlineMissRatePercent is < 0 or > 100 ||
            criteria.MaximumFailureRatePercent is < 0 or > 100 ||
            criteria.MaximumFleetUtilizationPercent is < 0 or > 100)
        {
            throw new ArgumentException("百分比验收门槛必须在 0 到 100 之间", nameof(criteria));
        }
    }
}
