namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Application.Services;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;

[ApiController]
[Route("api")]
public class WcsController : ControllerBase
{
    private readonly WcsApplicationService _wcs;
    private readonly ITaskScheduler _taskScheduler;

    public WcsController(WcsApplicationService wcs, ITaskScheduler taskScheduler)
    {
        _wcs = wcs;
        _taskScheduler = taskScheduler;
    }

    [HttpGet("overview")]
    public ActionResult<SystemOverview> GetOverview() => Ok(_wcs.GetOverview());

    [HttpGet("devices")]
    public ActionResult<IEnumerable<DeviceState>> GetDevices() => Ok(_wcs.GetAllDevices());

    [HttpGet("devices/{deviceId}")]
    public ActionResult<DeviceState?> GetDevice(string deviceId)
    {
        var device = _wcs.GetDevice(deviceId);
        if (device is null) return NotFound();
        return Ok(device);
    }

    [HttpGet("tasks")]
    public ActionResult<IEnumerable<TaskContext>> GetTasks() => Ok(_wcs.GetActiveTasks());

    [HttpGet("tasks/queue")]
    public ActionResult GetTaskQueue()
    {
        var queued = _taskScheduler.GetQueueCount();
        var running = _wcs.GetActiveTasks().Count();
        return Ok(new
        {
            queued,
            running,
            total = queued + running
        });
    }

    [HttpGet("tasks/{taskId}")]
    public ActionResult<TaskStatusEnum?> GetTaskStatus(string taskId)
    {
        var status = _wcs.GetTaskStatus(taskId);
        if (status is null) return NotFound();
        return Ok(status);
    }

    [HttpPost("tasks")]
    public async Task<ActionResult<TaskContext>> CreateTask(
        [FromBody] CreateTaskRequest request,
        CancellationToken ct)
    {
        var task = await _wcs.CreateTaskAsync(
            request.DeviceId, request.RouteId,
            request.Priority, request.Parameters, ct);
        return Ok(task);
    }

    [HttpPost("tasks/{taskId}/cancel")]
    public async Task<ActionResult<bool>> CancelTask(string taskId, CancellationToken ct)
    {
        var result = await _wcs.CancelTaskAsync(taskId, ct);
        return Ok(result);
    }

    [HttpPost("tasks/{taskId}/complete")]
    public async Task<ActionResult> CompleteTask(
        string taskId, [FromBody] CompleteTaskRequest request, CancellationToken ct)
    {
        await _wcs.CompleteTaskAsync(taskId, request.Success, request.Error, request.Result, ct);
        return Ok();
    }

    [HttpGet("alarms")]
    public ActionResult<IEnumerable<AlarmState>> GetAlarms() => Ok(_wcs.GetActiveAlarms());

    [HttpPost("alarms/{alarmId}/ack")]
    public async Task<ActionResult> AckAlarm(string alarmId, CancellationToken ct)
    {
        await _wcs.AckAlarmAsync(alarmId, ct);
        return Ok();
    }

    [HttpPost("alarms/{alarmCode}/recover")]
    public async Task<ActionResult> RecoverAlarm(string alarmCode, CancellationToken ct)
    {
        await _wcs.RecoverAlarmAsync(alarmCode, ct);
        return Ok();
    }

    // ===== 报警 DB 查询 =====

    [HttpGet("alarms/db")]
    public async Task<ActionResult<List<AlarmState>>> GetAlarmsFromDb()
        => Ok(await _wcs.GetAlarmsFromDbAsync());

    [HttpGet("alarms/history")]
    public async Task<ActionResult<PagedResult<AlarmState>>> GetAlarmHistory(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] string? level = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => Ok(await _wcs.GetAlarmHistoryAsync(from, to, level, page, pageSize, ct));

    // ===== 设备 DB 查询 =====

    [HttpGet("devices/db")]
    public async Task<ActionResult<List<DeviceState>>> GetDevicesFromDb()
        => Ok(await _wcs.GetDevicesFromDbAsync());

    // ===== 任务 DB 查询 =====

    [HttpGet("tasks/db")]
    public async Task<ActionResult<List<TaskContext>>> GetTasksFromDb()
        => Ok(await _wcs.GetTasksFromDbAsync());

    [HttpGet("tasks/history")]
    public async Task<ActionResult<PagedResult<TaskContext>>> GetTaskHistory(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] string? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
        => Ok(await _wcs.GetTaskHistoryAsync(from, to, status, page, pageSize, ct));

    [HttpGet("objects")]
    public ActionResult<IEnumerable<ObjectState>> GetObjects()
    {
        var objects = _wcs.GetAllDevices()
            .Select(d => _wcs.GetDevice(d.DeviceId))
            .OfType<DeviceState>()
            .ToList();
        return Ok(objects);
    }

    [HttpGet("objects/{objectId}")]
    public ActionResult<ObjectState?> GetObject(string objectId)
    {
        var obj = _wcs.GetObject(objectId);
        if (obj is null) return NotFound();
        return Ok(obj);
    }

    [HttpGet("locks")]
    public ActionResult GetLocks() => Ok(new { });

    [HttpPost("system/recover")]
    public async Task<ActionResult> Recover(CancellationToken ct)
    {
        var result = await _wcs.RecoverAsync(ct);
        return Ok(result);
    }
}

public record CreateTaskRequest(
    string DeviceId,
    string RouteId,
    int Priority = 2,
    Dictionary<string, object>? Parameters = null);

public record CompleteTaskRequest(
    bool Success,
    string? Error = null,
    object? Result = null);
