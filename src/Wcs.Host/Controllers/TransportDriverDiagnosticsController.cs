namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/drivers")]
public sealed class TransportDriverDiagnosticsController : ControllerBase
{
    private readonly ITransportPlcSignalMapService _maps;
    private readonly ITransportPlcSignalMapRegistry _mapRegistry;
    private readonly ITransportDriverDiagnosticsService _diagnostics;
    private readonly ITransportDriverSynchronizationService _synchronization;
    private readonly ITransportOperationGovernanceService _governance;
    private readonly ITransportCommandDispatcher _commands;

    public TransportDriverDiagnosticsController(
        ITransportPlcSignalMapService maps,
        ITransportPlcSignalMapRegistry mapRegistry,
        ITransportDriverDiagnosticsService diagnostics,
        ITransportDriverSynchronizationService synchronization,
        ITransportOperationGovernanceService governance,
        ITransportCommandDispatcher commands)
    {
        _maps = maps;
        _mapRegistry = mapRegistry;
        _diagnostics = diagnostics;
        _synchronization = synchronization;
        _governance = governance;
        _commands = commands;
    }

    [HttpGet("maps")]
    public async Task<ActionResult<IReadOnlyList<TransportPlcSignalMap>>> GetMaps(
        CancellationToken cancellationToken) =>
        Ok(await _maps.GetAllAsync(cancellationToken).ConfigureAwait(false));

    [HttpPut("maps/{vehicleId}")]
    public async Task<ActionResult<TransportPlcSignalMapSaveResult>> SaveMap(
        string vehicleId,
        [FromBody] SaveTransportPlcSignalMapRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(vehicleId, request.Map.VehicleId, StringComparison.Ordinal))
            return BadRequest("路由 VehicleId 与点位映射 VehicleId 不一致");

        var actor = TransportOperatorIdentityFactory.Create(User);
        var targetId = $"plc-map:{vehicleId}";
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.ChangeConfiguration,
            targetId,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _maps.SaveAndApplyAsync(
            request.Map,
            request.ExpectedVersion,
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        await _governance.CompleteExecutionAsync(
            request.OperationId,
            actor,
            result.Success,
            result.Success ? "PLC 点位映射已保存并应用" : result.Error ?? "保存失败",
            cancellationToken).ConfigureAwait(false);

        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpDelete("maps/{vehicleId}")]
    public async Task<IActionResult> DeleteMap(
        string vehicleId,
        [FromQuery] long expectedVersion,
        [FromQuery] string operationId,
        CancellationToken cancellationToken)
    {
        var actor = TransportOperatorIdentityFactory.Create(User);
        var begin = await _governance.BeginExecutionAsync(
            operationId,
            TransportGovernedOperationType.ChangeConfiguration,
            $"plc-map:{vehicleId}",
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var deleted = await _maps.DeleteAndApplyAsync(
            vehicleId,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        await _governance.CompleteExecutionAsync(
            operationId,
            actor,
            deleted,
            deleted ? "PLC 点位映射已删除" : "版本冲突或映射不存在",
            cancellationToken).ConfigureAwait(false);
        return deleted ? NoContent() : Conflict();
    }

    [HttpGet("diagnostics")]
    public ActionResult<IReadOnlyList<TransportDriverDiagnosticSnapshot>> GetDiagnostics() =>
        Ok(_diagnostics.GetAll());

    [HttpPost("poll")]
    public async Task<ActionResult<TransportDriverSyncReport>> Poll(
        CancellationToken cancellationToken) =>
        Ok(await _synchronization.PollAllAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("reconcile")]
    public async Task<ActionResult<TransportDriverReconciliationReport>> Reconcile(
        CancellationToken cancellationToken) =>
        Ok(await _synchronization.ReconcileAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("vehicles/{vehicleId}/manual-command")]
    public async Task<ActionResult<TransportCommandRecord>> SendManualCommand(
        string vehicleId,
        [FromBody] SendTransportManualCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!_mapRegistry.TryGet(vehicleId, out var map) || map is null)
            return NotFound("车辆没有驱动映射");

        var actor = TransportOperatorIdentityFactory.Create(User);
        var begin = await _governance.BeginExecutionAsync(
            request.OperationId,
            TransportGovernedOperationType.SendManualDriverCommand,
            vehicleId,
            actor,
            cancellationToken).ConfigureAwait(false);
        if (!begin.Success)
            return Conflict(begin);

        var command = new TransportExecutionCommand
        {
            RequestId = string.IsNullOrWhiteSpace(request.RequestId)
                ? $"manual:{request.OperationId}"
                : request.RequestId,
            VehicleId = vehicleId,
            CommandType = request.CommandType,
            TargetNodeId = request.TargetNodeId
        };
        var result = await _commands.DispatchAsync(
            command,
            map.Kind,
            maxRetries: 0,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var success = result.Status is TransportCommandStatus.Acknowledged or TransportCommandStatus.Completed;

        await _governance.CompleteExecutionAsync(
            request.OperationId,
            actor,
            success,
            success ? $"手动命令状态：{result.Status}" : result.Error ?? $"手动命令状态：{result.Status}",
            cancellationToken).ConfigureAwait(false);
        return success ? Ok(result) : Conflict(result);
    }
}

public sealed record SaveTransportPlcSignalMapRequest(
    string OperationId,
    long ExpectedVersion,
    TransportPlcSignalMap Map);

public sealed record SendTransportManualCommandRequest(
    string OperationId,
    TransportExecutionCommandType CommandType,
    string? TargetNodeId = null,
    string? RequestId = null);
