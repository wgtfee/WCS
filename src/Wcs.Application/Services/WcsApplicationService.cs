namespace Wcs.Application.Services;

using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.ObjectTracking;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.Recovery;
using Wcs.Core.ResourceLock;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.StateMachine;
using Wcs.Core.TaskEngine.Chain;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Orchestrator;
using Wcs.Core.TaskEngine.Scheduler;
using Wcs.Core.Persistence;

/// <summary>
/// WCS 应用服务 - 聚合核心模块的高层 API
/// </summary>
public class WcsApplicationService
{
    private readonly IStateCenter _stateCenter;
    private readonly IEventBus _eventBus;
    private readonly ITaskScheduler _taskScheduler;
    private readonly ITaskOrchestrator _taskOrchestrator;
    private readonly ITaskChainEngine _taskChainEngine;
    private readonly IAlarmCenter _alarmCenter;
    private readonly IObjectTrackingCenter _objectTracking;
    private readonly IResourceLockManager _resourceLock;
    private readonly IRecoveryManager _recoveryManager;
    private readonly IIdempotencyManager _idempotency;
    private readonly IAlarmQueryService _alarmQuery;
    private readonly ITaskQueryService _taskQuery;
    private readonly IDeviceQueryService _deviceQuery;

    public WcsApplicationService(
        IStateCenter stateCenter,
        IEventBus eventBus,
        ITaskScheduler taskScheduler,
        ITaskOrchestrator taskOrchestrator,
        ITaskChainEngine taskChainEngine,
        IAlarmCenter alarmCenter,
        IObjectTrackingCenter objectTracking,
        IResourceLockManager resourceLock,
        IRecoveryManager recoveryManager,
        IIdempotencyManager idempotency,
        IAlarmQueryService alarmQuery,
        ITaskQueryService taskQuery,
        IDeviceQueryService deviceQuery)
    {
        _stateCenter = stateCenter;
        _eventBus = eventBus;
        _taskScheduler = taskScheduler;
        _taskOrchestrator = taskOrchestrator;
        _taskChainEngine = taskChainEngine;
        _alarmCenter = alarmCenter;
        _objectTracking = objectTracking;
        _resourceLock = resourceLock;
        _recoveryManager = recoveryManager;
        _idempotency = idempotency;
        _alarmQuery = alarmQuery;
        _taskQuery = taskQuery;
        _deviceQuery = deviceQuery;
    }

    #region Tasks

    /// <summary>
    /// 创建并提交任务
    /// </summary>
    public async Task<TaskContext> CreateTaskAsync(string deviceId, string routeId,
        int priority = 2, Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        var task = new TaskContext
        {
            DeviceId = deviceId,
            RouteId = routeId,
            Priority = priority,
            Parameters = parameters ?? new Dictionary<string, object>(),
            Status = TaskStatusEnum.Created
        };

        // 幂等检查
        if (_idempotency.IsTaskProcessed(task.TaskId))
        {
            var existing = _idempotency.GetTaskResult(task.TaskId);
            task.Status = TaskStatusEnum.Completed;
            task.Result = existing?.Result;
            return task;
        }

        // 状态转移 Created -> Queued
        var sm = new TaskStateMachine();
        sm.TryTransitionTo(TaskStatusEnum.Queued, out _);

        _stateCenter.UpdateTaskRuntime(task.TaskId, new TaskRuntime
        {
            TaskId = task.TaskId,
            Status = TaskStatusEnum.Queued,
            Priority = priority,
            RouteId = routeId,
            CreatedTime = DateTime.UtcNow,
            Parameters = task.Parameters
        });

        await _eventBus.PublishAsync(new TaskCreatedEvent
        {
            TaskId = task.TaskId,
            TaskPriority = priority,
            RouteId = routeId,
            Parameters = task.Parameters
        }, ct);

        await _taskScheduler.EnqueueAsync(task, ct);
        return task;
    }

    /// <summary>
    /// 获取任务状态
    /// </summary>
    public TaskStatusEnum? GetTaskStatus(string taskId) => _taskOrchestrator.GetTaskStatus(taskId);

    /// <summary>
    /// 获取全部活跃任务
    /// </summary>
    public IEnumerable<TaskContext> GetActiveTasks() => _taskOrchestrator.GetActiveTasks();

    /// <summary>
    /// 取消任务
    /// </summary>
    public async Task<bool> CancelTaskAsync(string taskId, CancellationToken ct = default)
        => await _taskOrchestrator.CancelTaskAsync(taskId, ct);

    /// <summary>
    /// 完成任务并归档
    /// </summary>
    public async Task CompleteTaskAsync(string taskId, bool success, string? error = null,
        object? result = null, CancellationToken ct = default)
    {
        await _taskOrchestrator.CompleteTaskAsync(taskId, success, error, result, ct);

        await _eventBus.PublishAsync(new TaskCompletedEvent
        {
            TaskId = taskId,
            Success = success,
            ErrorMessage = error,
            EndTime = DateTime.UtcNow
        }, ct);

        // 记录幂等结果
        _idempotency.RecordTaskResult(taskId, new TaskIdempotencyResult
        {
            TaskId = taskId,
            ProcessedTime = DateTime.UtcNow,
            Success = success,
            Result = result,
            ErrorMessage = error
        });
    }

    /// <summary>
    /// 执行任务链
    /// </summary>
    public async Task<TaskChainResult> ExecuteChainAsync(TaskChain chain, CancellationToken ct = default)
        => await _taskChainEngine.ExecuteChainAsync(chain, ct);

    #endregion

    #region Devices

    /// <summary>
    /// 获取设备状态
    /// </summary>
    public DeviceState? GetDevice(string deviceId) => _stateCenter.GetDeviceState(deviceId);

    /// <summary>
    /// 获取所有设备状态
    /// </summary>
    public IEnumerable<DeviceState> GetAllDevices() => _stateCenter.GetAllDeviceStates();

    #endregion

    #region Alarms

    /// <summary>
    /// 产生报警
    /// </summary>
    public async Task RaiseAlarmAsync(string code, AlarmLevelEnum level, string msg,
        string? source = null, CancellationToken ct = default)
        => await _alarmCenter.RaiseAlarmAsync(code, level, msg, source: source, ct: ct);

    /// <summary>
    /// 确认报警
    /// </summary>
    public async Task AckAlarmAsync(string alarmId, CancellationToken ct = default)
        => await _alarmCenter.AcknowledgeAlarmAsync(alarmId, ct);

    /// <summary>
    /// 恢复报警
    /// </summary>
    public async Task RecoverAlarmAsync(string alarmCode, CancellationToken ct = default)
        => await _alarmCenter.RecoverAlarmAsync(alarmCode, ct);

    /// <summary>
    /// 获取活跃报警
    /// </summary>
    public IEnumerable<AlarmState> GetActiveAlarms() => _alarmCenter.GetActiveAlarms();

    #endregion

    #region Database Queries

    /// <summary>
    /// 从 Wcs_AlarmRuntime 表读取持久化的报警状态
    /// </summary>
    public async Task<List<AlarmState>> GetAlarmsFromDbAsync(CancellationToken ct = default)
    {
        var entities = await _alarmQuery.GetRuntimeAlarmsAsync(ct);
        return entities.Select(e => new AlarmState
        {
            AlarmId = e.AlarmId,
            AlarmCode = e.AlarmCode,
            Status = Enum.TryParse<AlarmStatusEnum>(e.Status, out var s) ? s : AlarmStatusEnum.Active,
            Level = Enum.TryParse<AlarmLevelEnum>(e.Level, out var l) ? l : AlarmLevelEnum.Info,
            Message = e.Message ?? string.Empty,
            OccurTime = e.OccurTime,
            RecoverTime = e.RecoverTime
        }).ToList();
    }

    /// <summary>
    /// 从 Wcs_AlarmHistory 表分页查询历史报警
    /// </summary>
    public async Task<PagedResult<AlarmState>> GetAlarmHistoryAsync(
        DateTime? from, DateTime? to, string? level,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        var (items, total) = await _alarmQuery.GetAlarmHistoryAsync(from, to, level, page, pageSize, ct);
        return new PagedResult<AlarmState>
        {
            Items = items.Select(e => new AlarmState
            {
                AlarmId = e.Id.ToString(),
                AlarmCode = e.AlarmCode,
                Status = AlarmStatusEnum.Recovered,
                Level = Enum.TryParse<AlarmLevelEnum>(e.Level, out var l) ? l : AlarmLevelEnum.Info,
                Message = e.Message ?? string.Empty,
                OccurTime = e.StartTime,
                RecoverTime = e.EndTime
            }).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 从 Wcs_TaskRun 表读取持久化的任务运行记录
    /// </summary>
    public async Task<List<TaskContext>> GetTasksFromDbAsync(CancellationToken ct = default)
    {
        var entities = await _taskQuery.GetTaskRunsAsync(ct);
        return entities.Select(e => new TaskContext
        {
            TaskId = e.TaskId,
            DeviceId = e.DeviceId ?? string.Empty,
            RouteId = e.RouteId ?? string.Empty,
            Status = (TaskStatusEnum)e.Status,
            Priority = e.Priority,
            CreatedTime = e.CreatedTime,
            StartTime = e.StartTime,
            EndTime = e.EndTime,
            ErrorMessage = e.ErrorMessage,
            RetryCount = e.RetryCount
        }).ToList();
    }

    /// <summary>
    /// 从 Wcs_TaskHistory 表分页查询历史任务
    /// </summary>
    public async Task<PagedResult<TaskContext>> GetTaskHistoryAsync(
        DateTime? from, DateTime? to, string? status,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        var (items, total) = await _taskQuery.GetTaskHistoryAsync(from, to, status, page, pageSize, ct);
        return new PagedResult<TaskContext>
        {
            Items = items.Select(e => new TaskContext
            {
                TaskId = e.TaskId,
                RouteId = e.RouteId ?? string.Empty,
                Priority = e.Priority,
                Status = e.Success ? TaskStatusEnum.Completed : TaskStatusEnum.Failed,
                StartTime = e.StartTime,
                EndTime = e.EndTime,
                ErrorMessage = e.ErrorMessage
            }).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// 从 Wcs_DeviceRuntime 表读取持久化的设备状态
    /// </summary>
    public async Task<List<DeviceState>> GetDevicesFromDbAsync(CancellationToken ct = default)
    {
        var entities = await _deviceQuery.GetDeviceRuntimesAsync(ct);
        return entities.Select(e => new DeviceState
        {
            DeviceId = e.DeviceId,
            Status = Enum.TryParse<DeviceStatusEnum>(e.Status, out var s) ? s : DeviceStatusEnum.Offline,
            LastUpdateTime = e.LastUpdateTime
        }).ToList();
    }

    #endregion

    #region Object Tracking

    /// <summary>
    /// 跟踪物料
    /// </summary>
    public void TrackObject(string objectId, string position, string? target = null)
        => _objectTracking.TrackObject(objectId, position, target);

    /// <summary>
    /// 移动物料
    /// </summary>
    public void MoveObject(string objectId, string newPosition)
        => _objectTracking.MoveObject(objectId, newPosition);

    /// <summary>
    /// 获取物料位置
    /// </summary>
    public ObjectState? GetObject(string objectId) => _objectTracking.GetObject(objectId);

    #endregion

    #region Resource Lock

    /// <summary>
    /// 获取资源锁
    /// </summary>
    public bool TryAcquireLock(string resource, string owner, int timeoutMs = 0)
        => _resourceLock.TryAcquire(resource, owner, timeoutMs);

    /// <summary>
    /// 释放资源锁
    /// </summary>
    public void ReleaseLock(string resource, string owner)
        => _resourceLock.Release(resource, owner);

    #endregion

    #region System

    /// <summary>
    /// 恢复系统
    /// </summary>
    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct = default)
        => await _recoveryManager.RecoverAsync(ct);

    /// <summary>
    /// 获取系统概览
    /// </summary>
    public SystemOverview GetOverview()
    {
        return new SystemOverview
        {
            DeviceCount = _stateCenter.GetAllDeviceStates().Count(),
            ActiveTaskCount = _stateCenter.GetAllActiveTasks().Count(),
            ActiveAlarmCount = _alarmCenter.GetActiveCount(),
            TrackedObjectCount = _objectTracking.Count,
            ActiveLockCount = _resourceLock.GetAllLocks().Count
        };
    }

    #endregion
}
