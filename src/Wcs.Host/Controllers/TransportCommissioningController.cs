namespace Wcs.Host.Controllers;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/commissioning")]
public sealed class TransportCommissioningController : ControllerBase
{
    private readonly ITransportPointTableImporter _importer;
    private readonly ITransportPlcSignalMapService _maps;
    private readonly ITransportSignalTemplateService _templates;
    private readonly ITransportCommissioningService _commissioning;
    private readonly ITransportFaultCatalogService _faults;
    private readonly ITransportRecoveryConflictService _conflicts;
    private readonly ITransportCommandCompensationService _compensation;
    private readonly ITransportDriverDiagnosticsService _diagnostics;
    private readonly ITransportOperationGovernanceService _governance;

    public TransportCommissioningController(
        ITransportPointTableImporter importer,
        ITransportPlcSignalMapService maps,
        ITransportSignalTemplateService templates,
        ITransportCommissioningService commissioning,
        ITransportFaultCatalogService faults,
        ITransportRecoveryConflictService conflicts,
        ITransportCommandCompensationService compensation,
        ITransportDriverDiagnosticsService diagnostics,
        ITransportOperationGovernanceService governance)
    {
        _importer = importer;
        _maps = maps;
        _templates = templates;
        _commissioning = commissioning;
        _faults = faults;
        _conflicts = conflicts;
        _compensation = compensation;
        _diagnostics = diagnostics;
        _governance = governance;
    }

    [HttpPost("point-table/validate")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<TransportPointTableImportResult>> ValidatePointTable(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
            return BadRequest("点位表为空");
        await using var memory = new MemoryStream();
        await file.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return Ok(_importer.Import(memory.ToArray(), file.FileName));
    }

    [HttpPost("point-table/apply")]
    public async Task<ActionResult<IReadOnlyList<TransportPlcSignalMapSaveResult>>> ApplyPointTable(
        [FromBody] ApplyTransportPointTableRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Maps.Count == 0)
            return BadRequest("没有需要应用的点位映射");
        if (request.Maps.Select(x => x.VehicleId).Distinct(StringComparer.Ordinal).Count() != request.Maps.Count)
            return BadRequest("批量映射中 VehicleId 重复");

        var actor = TransportOperatorIdentityFactory.Create(User);
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            "point-table:bulk",
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var current = await _maps.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var conflicts = request.Maps
            .Where(candidate =>
                (current.FirstOrDefault(x => string.Equals(x.VehicleId, candidate.VehicleId, StringComparison.Ordinal))?.Version ?? 0)
                != candidate.Version)
            .Select(x => x.VehicleId)
            .ToArray();
        if (conflicts.Length > 0)
        {
            var message = $"点位映射版本冲突：{string.Join(", ", conflicts)}";
            await CompleteAsync(request.OperationId, actor, false, message, cancellationToken).ConfigureAwait(false);
            return Conflict(message);
        }

        var results = new List<TransportPlcSignalMapSaveResult>();
        foreach (var map in request.Maps)
        {
            var result = await _maps.SaveAndApplyAsync(
                map,
                map.Version,
                actor.UserId,
                cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.Success)
                break;
        }

        var success = results.Count == request.Maps.Count && results.All(x => x.Success);
        await CompleteAsync(
            request.OperationId,
            actor,
            success,
            success ? $"已应用 {results.Count} 辆车点位映射" : results.LastOrDefault()?.Error ?? "批量应用失败",
            cancellationToken).ConfigureAwait(false);
        return success ? Ok(results) : Conflict(results);
    }

    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<TransportSignalTemplate>>> GetTemplates(
        CancellationToken cancellationToken) =>
        Ok(await _templates.GetAllAsync(cancellationToken).ConfigureAwait(false));

    [HttpPut("templates/{templateId}")]
    public async Task<ActionResult<TransportVersionedSaveResult<TransportSignalTemplate>>> SaveTemplate(
        string templateId,
        [FromBody] SaveTransportSignalTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var target = $"template:{templateId}";
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            target,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _templates.SaveAsync(
            request.Template with { TemplateId = templateId },
            request.ExpectedVersion,
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            request.OperationId,
            actor,
            result.Success,
            result.Success ? "点位模板已保存" : result.Error ?? "保存失败",
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpPost("templates/{templateId}/apply")]
    public async Task<ActionResult<TransportPlcSignalMapSaveResult>> ApplyTemplate(
        string templateId,
        [FromBody] ApplyTransportSignalTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var target = $"template-apply:{request.VehicleId}";
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            target,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _templates.ApplyAsync(
            templateId,
            request.VehicleId,
            request.DriverId,
            request.ExpectedMapVersion,
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            request.OperationId,
            actor,
            result.Success,
            result.Success ? "点位模板已应用" : result.Error ?? "应用失败",
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpGet("vehicles/{vehicleId}/probe")]
    public async Task<ActionResult<TransportSignalProbeResult>> Probe(
        string vehicleId,
        CancellationToken cancellationToken) =>
        Ok(await _commissioning.ProbeAsync(vehicleId, cancellationToken).ConfigureAwait(false));

    [HttpGet("vehicles/{vehicleId}/signals/read")]
    public async Task<ActionResult<TransportSignalValueResult>> ReadSignal(
        string vehicleId,
        [FromQuery] string tag,
        CancellationToken cancellationToken) =>
        Ok(await _commissioning.ReadSignalAsync(vehicleId, tag, cancellationToken).ConfigureAwait(false));

    [HttpPost("vehicles/{vehicleId}/signals/write")]
    public async Task<ActionResult<TransportSignalValueResult>> WriteSignal(
        string vehicleId,
        [FromBody] WriteTransportSignalRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var target = $"signal:{vehicleId}:{request.Tag}";
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.WritePlcSignal,
            target,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _commissioning.WriteSignalAsync(
            vehicleId,
            request.Tag,
            request.Value,
            cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            request.OperationId,
            actor,
            result.Success,
            result.Success ? "PLC 单点写入完成" : result.Error ?? "写入失败",
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpGet("traces")]
    public ActionResult<IReadOnlyList<TransportCommunicationTrace>> GetTraces(
        [FromQuery] int maxCount = 500,
        [FromQuery] string? driverId = null,
        [FromQuery] string? vehicleId = null) =>
        Ok(_commissioning.GetTraces(maxCount, driverId, vehicleId));

    [HttpGet("faults")]
    public async Task<ActionResult<IReadOnlyList<TransportFaultDefinition>>> GetFaults(
        CancellationToken cancellationToken) =>
        Ok(await _faults.GetAllAsync(cancellationToken).ConfigureAwait(false));

    [HttpPut("faults/{definitionId}")]
    public async Task<ActionResult<TransportVersionedSaveResult<TransportFaultDefinition>>> SaveFault(
        string definitionId,
        [FromBody] SaveTransportFaultRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var target = $"fault:{definitionId}";
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            target,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _faults.SaveAsync(
            request.Definition with { DefinitionId = definitionId },
            request.ExpectedVersion,
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            request.OperationId,
            actor,
            result.Success,
            result.Success ? "故障码定义已保存" : result.Error ?? "保存失败",
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpGet("conflicts")]
    public async Task<ActionResult<IReadOnlyList<TransportRecoveryConflictCase>>> GetConflicts(
        CancellationToken cancellationToken) =>
        Ok(await _conflicts.GetAllAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("conflicts/refresh")]
    public async Task<ActionResult<IReadOnlyList<TransportRecoveryConflictCase>>> RefreshConflicts(
        CancellationToken cancellationToken) =>
        Ok(await _conflicts.RefreshAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("conflicts/{caseId}/resolve")]
    public async Task<ActionResult<TransportRecoveryConflictResult>> ResolveConflict(
        string caseId,
        [FromBody] ResolveTransportRecoveryConflictRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var target = $"recovery:{caseId}";
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ResolveRecoveryConflict,
            target,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _conflicts.ResolveAsync(
            caseId,
            request.Resolution,
            request.Reason,
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            request.OperationId,
            actor,
            result.Success,
            result.Success ? "恢复冲突已处置" : result.Error ?? "处置失败",
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpGet("compensation")]
    public async Task<ActionResult<TransportCommandCompensationReport>> EvaluateCompensation(
        CancellationToken cancellationToken) =>
        Ok(await _compensation.EvaluateAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("compensation/{commandId}/retry-stop")]
    public async Task<ActionResult<TransportCommandRecord>> RetryStop(
        string commandId,
        [FromBody] RetryTransportCommandRequest request,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var target = $"compensate:{commandId}";
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.RetryCommandCompensation,
            target,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        try
        {
            var result = await _compensation.RetrySafeStopAsync(commandId, cancellationToken).ConfigureAwait(false);
            var success = result.Status is TransportCommandStatus.Acknowledged or TransportCommandStatus.Completed;
            await CompleteAsync(
                request.OperationId,
                actor,
                success,
                success ? "Stop 命令补偿完成" : result.Error ?? $"补偿状态：{result.Status}",
                cancellationToken).ConfigureAwait(false);
            return success ? Ok(result) : Conflict(result);
        }
        catch (Exception ex)
        {
            await CompleteAsync(request.OperationId, actor, false, ex.Message, cancellationToken).ConfigureAwait(false);
            return Conflict(ex.Message);
        }
    }

    [HttpGet("report/export")]
    public async Task<IActionResult> ExportReport(CancellationToken cancellationToken)
    {
        var report = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Maps = await _maps.GetAllAsync(cancellationToken).ConfigureAwait(false),
            Diagnostics = _diagnostics.GetAll(),
            Faults = await _faults.GetAllAsync(cancellationToken).ConfigureAwait(false),
            Conflicts = await _conflicts.GetAllAsync(cancellationToken).ConfigureAwait(false),
            Compensation = await _compensation.EvaluateAsync(cancellationToken).ConfigureAwait(false),
            Traces = _commissioning.GetTraces(500)
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var payload = JsonSerializer.SerializeToUtf8Bytes(report, options);
        return File(
            payload,
            "application/json",
            $"transport-commissioning-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
    }

    private Task CompleteAsync(
        string operationId,
        TransportOperatorIdentity actor,
        bool success,
        string message,
        CancellationToken cancellationToken) =>
        _governance.CompleteExecutionAsync(
            operationId,
            actor,
            success,
            message,
            cancellationToken);
}

public sealed record ApplyTransportPointTableRequest(
    string OperationId,
    IReadOnlyList<TransportPlcSignalMap> Maps);

public sealed record SaveTransportSignalTemplateRequest(
    string OperationId,
    long ExpectedVersion,
    TransportSignalTemplate Template);

public sealed record ApplyTransportSignalTemplateRequest(
    string OperationId,
    string VehicleId,
    string DriverId,
    long ExpectedMapVersion);

public sealed record WriteTransportSignalRequest(
    string OperationId,
    string Tag,
    object? Value);

public sealed record SaveTransportFaultRequest(
    string OperationId,
    long ExpectedVersion,
    TransportFaultDefinition Definition);

public sealed record ResolveTransportRecoveryConflictRequest(
    string OperationId,
    TransportRecoveryResolution Resolution,
    string Reason);

public sealed record RetryTransportCommandRequest(string OperationId);
