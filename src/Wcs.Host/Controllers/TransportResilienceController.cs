namespace Wcs.Host.Controllers;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Wcs.Core.TransportScheduling;

[ApiController]
[Route("api/transport/resilience")]
public sealed class TransportResilienceController : ControllerBase
{
    private readonly ITransportResilienceService _service;

    public TransportResilienceController(ITransportResilienceService service)
    {
        _service = service;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<TransportResilienceSnapshot>> GetSummary(
        CancellationToken cancellationToken)
    {
        var backups = await _service.GetBackupsAsync(100, cancellationToken).ConfigureAwait(false);
        var baselines = _service.GetBaselines(100);
        var drills = _service.GetDrills(100);
        return Ok(new TransportResilienceSnapshot
        {
            LastReadiness = _service.GetLastReadiness(),
            LastBaseline = baselines.FirstOrDefault(),
            LastBackup = backups.FirstOrDefault(),
            LastDrill = drills.FirstOrDefault(),
            BackupCount = backups.Count,
            DrillCount = drills.Count
        });
    }

    [HttpGet("readiness")]
    public ActionResult<TransportReadinessReport?> GetReadiness() =>
        Ok(_service.GetLastReadiness());

    [HttpPost("readiness/run")]
    public async Task<ActionResult<TransportReadinessReport>> RunReadiness(
        CancellationToken cancellationToken) =>
        Ok(await _service.RunPreflightAsync(cancellationToken).ConfigureAwait(false));

    [HttpGet("baselines")]
    public ActionResult<IReadOnlyList<TransportOperationalBaseline>> GetBaselines(
        [FromQuery] int maxCount = 100) =>
        Ok(_service.GetBaselines(Math.Clamp(maxCount, 1, 100)));

    [HttpPost("baselines")]
    public async Task<ActionResult<TransportOperationalBaseline>> CaptureBaseline(
        [FromBody] CaptureTransportBaselineRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("运行基线必须记录经过认证的创建人");
        return Ok(await _service.CaptureBaselineAsync(
            request.Name,
            request.Reason,
            identity.UserId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("backups")]
    public async Task<ActionResult<IReadOnlyList<TransportLogicalBackupManifest>>> GetBackups(
        [FromQuery] int maxCount = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await _service.GetBackupsAsync(Math.Clamp(maxCount, 1, 1000), cancellationToken).ConfigureAwait(false));

    [HttpPost("backups")]
    public async Task<ActionResult<TransportLogicalBackupManifest>> CreateBackup(
        [FromBody] CreateTransportLogicalBackupRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("手动逻辑备份必须记录经过认证的创建人");
        return Ok(await _service.CreateBackupAsync(
            request.Name,
            request.Reason,
            identity.UserId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("backups/{backupId}/download")]
    public async Task<IActionResult> DownloadBackup(
        string backupId,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();
        var content = await _service.GetBackupContentAsync(backupId, cancellationToken).ConfigureAwait(false);
        if (content is null)
            return NotFound();
        return File(content.Payload, "application/json", content.Manifest.FileName);
    }

    [HttpPost("backups/{backupId}/validate")]
    public async Task<ActionResult<TransportBackupValidationReport>> ValidateBackup(
        string backupId,
        CancellationToken cancellationToken) =>
        Ok(await _service.ValidateBackupAsync(backupId, cancellationToken).ConfigureAwait(false));

    [HttpPost("backups/{backupId}/prepare-restore")]
    public async Task<ActionResult<TransportRestorePreparationResult>> PrepareRestore(
        string backupId,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();
        var result = await _service.PrepareRestoreAsync(
            backupId,
            identity.UserId,
            cancellationToken).ConfigureAwait(false);
        return result.Success ? Ok(result) : Conflict(result);
    }

    [HttpGet("drills")]
    public ActionResult<IReadOnlyList<TransportRecoveryDrillReport>> GetDrills(
        [FromQuery] int maxCount = 100) =>
        Ok(_service.GetDrills(Math.Clamp(maxCount, 1, 100)));

    [HttpPost("drills")]
    public async Task<ActionResult<TransportRecoveryDrillReport>> RunDrill(
        [FromBody] TransportRecoveryDrillRequest request,
        CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized("恢复演练必须记录经过认证的执行人");
        return Ok(await _service.RunDrillAsync(
            request,
            identity.UserId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("report/export")]
    public async Task<IActionResult> ExportReport(CancellationToken cancellationToken)
    {
        var identity = TransportOperatorIdentityFactory.Create(User);
        if (!identity.IsAuthenticated)
            return Unauthorized();
        var backups = await _service.GetBackupsAsync(100, cancellationToken).ConfigureAwait(false);
        var baselines = _service.GetBaselines(100);
        var drills = _service.GetDrills(100);
        var report = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = new TransportResilienceSnapshot
            {
                LastReadiness = _service.GetLastReadiness(),
                LastBaseline = baselines.FirstOrDefault(),
                LastBackup = backups.FirstOrDefault(),
                LastDrill = drills.FirstOrDefault(),
                BackupCount = backups.Count,
                DrillCount = drills.Count
            },
            Baselines = baselines,
            Backups = backups,
            Drills = drills
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(
            payload,
            "application/json",
            $"transport-resilience-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
    }
}

public sealed record CaptureTransportBaselineRequest(string Name, string Reason);
public sealed record CreateTransportLogicalBackupRequest(string Name, string Reason);
