namespace Wcs.Host.Controllers;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/simulation")]
public sealed class TransportSimulationController : ControllerBase
{
    private readonly ITransportSimulationService _service;

    public TransportSimulationController(ITransportSimulationService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    public ActionResult<TransportSimulationSummary> GetSummary() =>
        Ok(_service.GetSummary());

    [HttpPost("scenarios/current")]
    public async Task<ActionResult<TransportSimulationScenario>> BuildCurrentScenario(
        [FromBody] BuildCurrentTransportScenarioRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _service.BuildCurrentScenarioAsync(
            request.Name,
            request.HorizonSeconds,
            request.MaximumTasks,
            cancellationToken).ConfigureAwait(false));

    [HttpPost("scenarios/history")]
    public async Task<ActionResult<TransportSimulationScenario>> BuildHistoricalScenario(
        [FromBody] TransportHistoricalReplayRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _service.BuildHistoricalScenarioAsync(request, cancellationToken).ConfigureAwait(false));

    [HttpPost("runs")]
    public async Task<ActionResult<TransportSimulationRun>> Run(
        [FromBody] RunTransportSimulationRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("离线仿真必须记录经过认证的执行人");
        return Ok(await _service.RunAsync(
            request.Scenario,
            request.Policy,
            identity.UserId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("comparisons")]
    public async Task<ActionResult<TransportStrategyComparisonReport>> Compare(
        [FromBody] CompareTransportStrategiesRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("策略 A/B 对比必须记录经过认证的执行人");
        return Ok(await _service.CompareAsync(
            request.Scenario,
            request.Policies,
            identity.UserId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("optimizations")]
    public async Task<ActionResult<TransportBatchOptimizationResult>> Optimize(
        [FromBody] OptimizeTransportBatchRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("批量优化必须记录经过认证的执行人");
        return Ok(await _service.OptimizeBatchAsync(
            request.Scenario,
            identity.UserId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("capacity-benchmarks")]
    public async Task<ActionResult<TransportCapacityBenchmarkReport>> RunCapacityBenchmark(
        [FromBody] TransportCapacityBenchmarkRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("容量压力仿真必须记录经过认证的执行人");
        return Ok(await _service.RunCapacityBenchmarkAsync(
            request,
            identity.UserId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("acceptance-reports")]
    public async Task<ActionResult<TransportFinalAcceptanceReport>> GenerateAcceptanceReport(
        [FromBody] GenerateTransportAcceptanceReportRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("最终验收报告必须记录经过认证的执行人");
        return Ok(await _service.GenerateAcceptanceReportAsync(
            request.Name,
            request.SimulationRunId,
            request.Criteria,
            identity.UserId,
            request.ComparisonId,
            request.BenchmarkId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("runs")]
    public ActionResult<IReadOnlyList<TransportSimulationRun>> GetRuns(
        [FromQuery] int maxCount = 100) =>
        Ok(_service.GetRuns(Math.Clamp(maxCount, 1, 500)));

    [HttpGet("comparisons")]
    public ActionResult<IReadOnlyList<TransportStrategyComparisonReport>> GetComparisons(
        [FromQuery] int maxCount = 100) =>
        Ok(_service.GetComparisons(Math.Clamp(maxCount, 1, 500)));

    [HttpGet("optimizations")]
    public ActionResult<IReadOnlyList<TransportBatchOptimizationResult>> GetOptimizations(
        [FromQuery] int maxCount = 100) =>
        Ok(_service.GetOptimizations(Math.Clamp(maxCount, 1, 500)));

    [HttpGet("capacity-benchmarks")]
    public ActionResult<IReadOnlyList<TransportCapacityBenchmarkReport>> GetCapacityBenchmarks(
        [FromQuery] int maxCount = 100) =>
        Ok(_service.GetBenchmarks(Math.Clamp(maxCount, 1, 500)));

    [HttpGet("acceptance-reports")]
    public ActionResult<IReadOnlyList<TransportFinalAcceptanceReport>> GetAcceptanceReports(
        [FromQuery] int maxCount = 100) =>
        Ok(_service.GetAcceptanceReports(Math.Clamp(maxCount, 1, 500)));

    [HttpGet("report/export")]
    public IActionResult ExportFinalReport()
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();
        var report = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            GeneratedBy = identity.UserId,
            Summary = _service.GetSummary(),
            Runs = _service.GetRuns(200),
            Comparisons = _service.GetComparisons(100),
            Optimizations = _service.GetOptimizations(100),
            CapacityBenchmarks = _service.GetBenchmarks(100),
            AcceptanceReports = _service.GetAcceptanceReports(100),
            ProductionActivationNotice = "所有仿真推荐均为离线候选；生产参数变更必须走 ChangeConfiguration 双人审批。"
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(
            payload,
            "application/json",
            $"transport-final-acceptance-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
    }
}

public sealed record BuildCurrentTransportScenarioRequest(
    string Name,
    int HorizonSeconds = 3600,
    int MaximumTasks = 1000);

public sealed record RunTransportSimulationRequest(
    TransportSimulationScenario Scenario,
    TransportSimulationPolicy Policy);

public sealed record CompareTransportStrategiesRequest(
    TransportSimulationScenario Scenario,
    IReadOnlyList<TransportSimulationPolicy> Policies);

public sealed record OptimizeTransportBatchRequest(TransportSimulationScenario Scenario);

public sealed record GenerateTransportAcceptanceReportRequest(
    string Name,
    string SimulationRunId,
    TransportAcceptanceCriteria Criteria,
    string? ComparisonId = null,
    string? BenchmarkId = null);
