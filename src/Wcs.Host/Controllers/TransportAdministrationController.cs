namespace Wcs.Host.Controllers;

using Industrial.Security.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/administration")]
public sealed class TransportAdministrationController : ControllerBase
{
    private readonly ITransportConfigurationService _configuration;
    private readonly ITransportOperationGovernanceService _governance;
    private readonly ITransportJournalStore _journal;
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly ITransportCommandDispatcher _commands;

    public TransportAdministrationController(
        ITransportConfigurationService configuration,
        ITransportOperationGovernanceService governance,
        ITransportJournalStore journal,
        ITransportTrafficCoordinator traffic,
        ITransportCommandDispatcher commands)
    {
        _configuration = configuration;
        _governance = governance;
        _journal = journal;
        _traffic = traffic;
        _commands = commands;
    }

    [HttpGet("configuration")]
    [Permission("WCS.Task.View")]
    public async Task<ActionResult<TransportRuntimeConfiguration>> GetConfiguration(CancellationToken cancellationToken) =>
        Ok(await _configuration.GetAsync(cancellationToken));

    [HttpGet("operations")]
    [Permission("WCS.Task.View")]
    public async Task<ActionResult<IReadOnlyList<TransportGovernedOperation>>> GetOperations(
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default) =>
        Ok(await _governance.GetOperationsAsync(Math.Clamp(maxCount, 1, 1000), cancellationToken));

    [HttpGet("audits")]
    [Permission("WCS.Task.View")]
    public async Task<ActionResult<IReadOnlyList<TransportAuditRecord>>> GetAudits(
        [FromQuery] int maxCount = 500,
        CancellationToken cancellationToken = default) =>
        Ok(await _governance.GetAuditsAsync(Math.Clamp(maxCount, 1, 2000), cancellationToken));

    [HttpGet("journal")]
    [Permission("WCS.Task.View")]
    public async Task<ActionResult<IReadOnlyList<TransportJournalRecord>>> GetJournal(
        [FromQuery] TransportJournalCategory? category = null,
        [FromQuery] int maxCount = 500,
        CancellationToken cancellationToken = default) =>
        Ok(await _journal.QueryAsync(category, Math.Clamp(maxCount, 1, 2000), cancellationToken));

    [HttpPost("operations")]
    [Permission("WCS.Task.Edit")]
    public async Task<ActionResult<TransportGovernedOperation>> RequestOperation(
        [FromBody] RequestTransportOperation request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("危险操作必须接入经过认证的用户身份");

        var result = await _governance.RequestAsync(
            request.OperationType,
            request.TargetId,
            request.Reason,
            identity,
            request.ValidMinutes.HasValue ? TimeSpan.FromMinutes(request.ValidMinutes.Value) : null,
            cancellationToken);

        if (result.Success && result.Operation is not null)
            return Ok(result.Operation);
        return ForbidOrBadRequest(result.Error);
    }

    [HttpPost("operations/{operationId}/approve")]
    [Permission("WCS.Task.Edit")]
    public async Task<ActionResult<TransportGovernedOperation>> ApproveOperation(
        string operationId,
        [FromBody] ApproveTransportOperation request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var result = await _governance.ApproveAsync(operationId, identity, request.Comment, cancellationToken);
        if (result.Success && result.Operation is not null)
            return Ok(result.Operation);
        return ForbidOrBadRequest(result.Error);
    }

    [HttpPost("operations/{operationId}/reject")]
    [Permission("WCS.Task.Edit")]
    public async Task<ActionResult<TransportGovernedOperation>> RejectOperation(
        string operationId,
        [FromBody] RejectTransportOperation request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var result = await _governance.RejectAsync(operationId, identity, request.Reason, cancellationToken);
        if (result.Success && result.Operation is not null)
            return Ok(result.Operation);
        return ForbidOrBadRequest(result.Error);
    }

    [HttpPut("configuration/{operationId}")]
    [Permission("WCS.Task.Edit")]
    public async Task<IActionResult> SaveConfiguration(
        string operationId,
        [FromBody] SaveTransportConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var begin = await _governance.BeginExecutionAsync(
            operationId,
            TransportGovernedOperationType.ChangeConfiguration,
            request.Configuration.ConfigurationId,
            identity,
            cancellationToken);
        if (!begin.Success)
            return Conflict(begin);

        var result = await _configuration.SaveAndApplyAsync(
            request.Configuration,
            request.ExpectedVersion,
            identity.UserId,
            cancellationToken);

        await _governance.CompleteExecutionAsync(
            operationId,
            identity,
            result.Success,
            result.Success ? $"配置已保存为版本 {result.Configuration!.Version}" : result.Error ?? "配置保存失败",
            cancellationToken);

        if (result.Success)
            return Ok(result.Configuration);
        return result.VersionConflict ? Conflict(result) : BadRequest(result);
    }

    [HttpPost("operations/{operationId}/execute/traffic/{ownerId}/force-release")]
    [Permission("WCS.RGV.ForceRelease")]
    public async Task<IActionResult> ForceReleaseTraffic(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var begin = await _governance.BeginExecutionAsync(
            operationId,
            TransportGovernedOperationType.ForceReleaseTraffic,
            ownerId,
            identity,
            cancellationToken);
        if (!begin.Success)
            return Conflict(begin);

        var released = _traffic.ReleaseOwner(ownerId, includeOccupied: true);
        await _governance.CompleteExecutionAsync(
            operationId,
            identity,
            true,
            $"已强制释放 {released.Count} 个交通资源",
            cancellationToken);
        return Ok(new { ownerId, releasedResourceIds = released });
    }

    [HttpPost("operations/{operationId}/execute/driver/{vehicleId}/command")]
    [Permission("WCS.RGV.Dispatch")]
    public async Task<IActionResult> SendManualDriverCommand(
        string operationId,
        string vehicleId,
        [FromBody] ManualTransportDriverCommand request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var begin = await _governance.BeginExecutionAsync(
            operationId,
            TransportGovernedOperationType.SendManualDriverCommand,
            vehicleId,
            identity,
            cancellationToken);
        if (!begin.Success)
            return Conflict(begin);

        try
        {
            var result = await _commands.DispatchAsync(new TransportExecutionCommand
            {
                RequestId = string.IsNullOrWhiteSpace(request.RequestId)
                    ? $"manual:{operationId}"
                    : request.RequestId,
                VehicleId = vehicleId,
                CommandType = request.CommandType,
                TargetNodeId = request.TargetNodeId
            }, request.VehicleKind, request.MaxRetries, cancellationToken);

            var success = result.Status is TransportCommandStatus.Acknowledged or TransportCommandStatus.Completed;
            await _governance.CompleteExecutionAsync(
                operationId,
                identity,
                success,
                success ? $"命令状态：{result.Status}" : result.Error ?? $"命令状态：{result.Status}",
                cancellationToken);
            return success ? Ok(result) : Conflict(result);
        }
        catch (Exception ex)
        {
            await _governance.CompleteExecutionAsync(operationId, identity, false, ex.Message, cancellationToken);
            throw;
        }
    }

    private ActionResult ForbidOrBadRequest(string? error) =>
        error?.Contains("权限", StringComparison.Ordinal) == true
            ? StatusCode(StatusCodes.Status403Forbidden, new { error })
            : BadRequest(new { error });
}

public sealed record RequestTransportOperation(
    TransportGovernedOperationType OperationType,
    string TargetId,
    string Reason,
    int? ValidMinutes = null);

public sealed record ApproveTransportOperation(string? Comment);
public sealed record RejectTransportOperation(string Reason);
public sealed record SaveTransportConfigurationRequest(
    long ExpectedVersion,
    TransportRuntimeConfiguration Configuration);
public sealed record ManualTransportDriverCommand(
    TransportVehicleKind VehicleKind,
    TransportExecutionCommandType CommandType,
    string? TargetNodeId = null,
    string? RequestId = null,
    int MaxRetries = 0);
