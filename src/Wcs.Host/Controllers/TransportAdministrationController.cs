namespace Wcs.Host.Controllers;

using System.Security.Claims;
using Industrial.Security.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Wcs.Core.TransportScheduling;
using Wcs.Host.IndustrialSecurity;

[ApiController]
[Route("api/transport/administration")]
public sealed class TransportAdministrationController : ControllerBase
{
    private readonly ITransportConfigurationService _configuration;
    private readonly ITransportOperationGovernanceService _governance;
    private readonly ITransportJournalStore _journal;
    private readonly ITransportTrafficCoordinator _traffic;
    private readonly ITransportCommandDispatcher _commands;
    private readonly IPermissionChecker _permissionChecker;
    private readonly IConfiguration _securityConfiguration;
    private readonly ILogger<TransportAdministrationController> _logger;

    public TransportAdministrationController(
        ITransportConfigurationService configuration,
        ITransportOperationGovernanceService governance,
        ITransportJournalStore journal,
        ITransportTrafficCoordinator traffic,
        ITransportCommandDispatcher commands,
        IPermissionChecker permissionChecker,
        IConfiguration securityConfiguration,
        ILogger<TransportAdministrationController> logger)
    {
        _configuration = configuration;
        _governance = governance;
        _journal = journal;
        _traffic = traffic;
        _commands = commands;
        _permissionChecker = permissionChecker;
        _securityConfiguration = securityConfiguration;
        _logger = logger;
    }

    [HttpGet("configuration")]
    [Permission(WcsManagementPermissionCodes.AdministrationView)]
    public async Task<ActionResult<TransportRuntimeConfiguration>> GetConfiguration(CancellationToken cancellationToken) =>
        Ok(await _configuration.GetAsync(cancellationToken));

    [HttpGet("operations")]
    [Permission(WcsManagementPermissionCodes.AdministrationView)]
    public async Task<ActionResult<IReadOnlyList<TransportGovernedOperation>>> GetOperations(
        [FromQuery] int maxCount = 200,
        CancellationToken cancellationToken = default) =>
        Ok(await _governance.GetOperationsAsync(Math.Clamp(maxCount, 1, 1000), cancellationToken));

    [HttpGet("audits")]
    [Permission(WcsManagementPermissionCodes.AdministrationView)]
    public async Task<ActionResult<IReadOnlyList<TransportAuditRecord>>> GetAudits(
        [FromQuery] int maxCount = 500,
        CancellationToken cancellationToken = default) =>
        Ok(await _governance.GetAuditsAsync(Math.Clamp(maxCount, 1, 2000), cancellationToken));

    [HttpGet("journal")]
    [Permission(WcsManagementPermissionCodes.AdministrationView)]
    public async Task<ActionResult<IReadOnlyList<TransportJournalRecord>>> GetJournal(
        [FromQuery] TransportJournalCategory? category = null,
        [FromQuery] int maxCount = 500,
        CancellationToken cancellationToken = default) =>
        Ok(await _journal.QueryAsync(category, Math.Clamp(maxCount, 1, 2000), cancellationToken));

    [HttpPost("operations")]
    [Permission(WcsManagementPermissionCodes.OperationManage)]
    public async Task<ActionResult<TransportGovernedOperation>> RequestOperation(
        [FromBody] RequestTransportOperation request,
        CancellationToken cancellationToken)
    {
        await ProjectIamGovernancePermissionsAsync(cancellationToken);
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

        LogMutation("RequestOperation", request.TargetId, identity.UserId, result.Success, result.Error);
        if (result.Success && result.Operation is not null)
            return Ok(result.Operation);
        return ForbidOrBadRequest(result.Error);
    }

    [HttpPost("operations/{operationId}/approve")]
    [Permission(WcsManagementPermissionCodes.OperationManage)]
    public async Task<ActionResult<TransportGovernedOperation>> ApproveOperation(
        string operationId,
        [FromBody] ApproveTransportOperation request,
        CancellationToken cancellationToken)
    {
        await ProjectIamGovernancePermissionsAsync(cancellationToken);
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var result = await _governance.ApproveAsync(operationId, identity, request.Comment, cancellationToken);
        LogMutation("ApproveOperation", operationId, identity.UserId, result.Success, result.Error);
        if (result.Success && result.Operation is not null)
            return Ok(result.Operation);
        return ForbidOrBadRequest(result.Error);
    }

    [HttpPost("operations/{operationId}/reject")]
    [Permission(WcsManagementPermissionCodes.OperationManage)]
    public async Task<ActionResult<TransportGovernedOperation>> RejectOperation(
        string operationId,
        [FromBody] RejectTransportOperation request,
        CancellationToken cancellationToken)
    {
        await ProjectIamGovernancePermissionsAsync(cancellationToken);
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();

        var result = await _governance.RejectAsync(operationId, identity, request.Reason, cancellationToken);
        LogMutation("RejectOperation", operationId, identity.UserId, result.Success, result.Error);
        if (result.Success && result.Operation is not null)
            return Ok(result.Operation);
        return ForbidOrBadRequest(result.Error);
    }

    [HttpPut("configuration/{operationId}")]
    [Permission(WcsManagementPermissionCodes.ConfigurationChange)]
    public async Task<IActionResult> SaveConfiguration(
        string operationId,
        [FromBody] SaveTransportConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        await ProjectIamGovernancePermissionsAsync(cancellationToken);
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
        {
            LogMutation("SaveConfiguration.Begin", request.Configuration.ConfigurationId, identity.UserId, false, begin.Error);
            return Conflict(begin);
        }

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

        LogMutation("SaveConfiguration", request.Configuration.ConfigurationId, identity.UserId, result.Success, result.Error);
        if (result.Success)
            return Ok(result.Configuration);
        return result.VersionConflict ? Conflict(result) : BadRequest(result);
    }

    [HttpPost("operations/{operationId}/execute/traffic/{ownerId}/force-release")]
    [Permission(WcsManagementPermissionCodes.TrafficForceRelease)]
    public async Task<IActionResult> ForceReleaseTraffic(
        string operationId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        await ProjectIamGovernancePermissionsAsync(cancellationToken);
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
        {
            LogMutation("ForceReleaseTraffic.Begin", ownerId, identity.UserId, false, begin.Error);
            return Conflict(begin);
        }

        var released = _traffic.ReleaseOwner(ownerId, includeOccupied: true);
        await _governance.CompleteExecutionAsync(
            operationId,
            identity,
            true,
            $"已强制释放 {released.Count} 个交通资源",
            cancellationToken);
        LogMutation("ForceReleaseTraffic", ownerId, identity.UserId, true, $"Released={released.Count}");
        return Ok(new { ownerId, releasedResourceIds = released });
    }

    [HttpPost("operations/{operationId}/execute/driver/{vehicleId}/command")]
    [Permission(WcsManagementPermissionCodes.VehicleManualCommand)]
    public async Task<IActionResult> SendManualDriverCommand(
        string operationId,
        string vehicleId,
        [FromBody] ManualTransportDriverCommand request,
        CancellationToken cancellationToken)
    {
        await ProjectIamGovernancePermissionsAsync(cancellationToken);
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
        {
            LogMutation("SendManualDriverCommand.Begin", vehicleId, identity.UserId, false, begin.Error);
            return Conflict(begin);
        }

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
            LogMutation("SendManualDriverCommand", vehicleId, identity.UserId, success, result.Error ?? result.Status.ToString());
            return success ? Ok(result) : Conflict(result);
        }
        catch (Exception ex)
        {
            await _governance.CompleteExecutionAsync(operationId, identity, false, ex.Message, cancellationToken);
            LogMutation("SendManualDriverCommand", vehicleId, identity.UserId, false, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Existing transport governance already makes the final dangerous-operation decision
    /// from TransportPermissions claims. Centralized IAM stores canonical wcs.* capabilities,
    /// so project only the capabilities granted to this human request back to those legacy
    /// claims before invoking governance. The method is never called by runtime workers.
    /// </summary>
    private async Task ProjectIamGovernancePermissionsAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_securityConfiguration["Security:Authentication:Mode"], "Centralized", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_securityConfiguration["Security:Authorization:Mode"], "Centralized", StringComparison.OrdinalIgnoreCase)
            || User.Identity?.IsAuthenticated != true)
            return;

        var claims = new List<Claim>();
        foreach (var capability in WcsManagementPermissionCodes.CanonicalToTransport)
        {
            if (string.Equals(capability.Key, WcsManagementPermissionCodes.OperationManage, StringComparison.OrdinalIgnoreCase))
                continue;

            if (await _permissionChecker.HasPermissionAsync(capability.Key, cancellationToken))
                claims.Add(new Claim("permission", capability.Value));
        }

        if (claims.Count > 0)
        {
            User.AddIdentity(new ClaimsIdentity(
                claims,
                "WCS.IamGovernanceOverlay",
                ClaimTypes.Name,
                ClaimTypes.Role));
        }
    }

    private void LogMutation(string action, string targetId, string userId, bool success, string? detail)
    {
        _logger.LogInformation(
            "WCS management mutation. Action={Action}; TargetId={TargetId}; UserId={UserId}; GlobalUserId={GlobalUserId}; Success={Success}; TraceId={TraceId}; Detail={Detail}",
            action,
            targetId,
            userId,
            User.FindFirstValue(IndustrialClaimTypes.GlobalUserId) ?? User.FindFirstValue("sub"),
            success,
            HttpContext.TraceIdentifier,
            detail);
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
