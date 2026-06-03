namespace Wcs.Core.WorkflowCenter;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wcs.Core.TaskEngine.Chain;
using Wcs.Core.TaskEngine.Context;
using Wcs.Core.TaskEngine.Scheduler;

/// <summary>
/// 流程中心实现 — 管理业务流程生命周期
/// </summary>
public class WorkflowCenter : IWorkflowCenter
{
    private readonly ConcurrentDictionary<string, WorkflowDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, WorkflowInstance> _instances = new();
    private readonly ITaskScheduler _scheduler;
    private readonly IWorkflowHook? _hook;
    private readonly ILogger<WorkflowCenter>? _logger;

    private long _totalCompleted;
    private long _totalFailed;

    /// <summary>
    /// 流程钩子 — 允许在流程各阶段插入自定义逻辑
    /// </summary>
    public interface IWorkflowHook
    {
        Task OnWorkflowStartingAsync(WorkflowInstance instance, CancellationToken ct);
        Task OnStageStartingAsync(WorkflowInstance instance, WorkflowStage stage, int stageIndex, CancellationToken ct);
        Task OnStageCompletedAsync(WorkflowInstance instance, WorkflowStageResult result, CancellationToken ct);
        Task OnWorkflowCompletedAsync(WorkflowInstance instance, CancellationToken ct);
    }

    public WorkflowCenter(
        ITaskScheduler scheduler,
        IWorkflowHook? hook = null,
        ILogger<WorkflowCenter>? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _hook = hook;
        _logger = logger;
    }

    public void RegisterDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definitions[definition.DefinitionId] = definition;
        _logger?.LogInformation("Registered workflow definition: {DefId} ({Name})",
            definition.DefinitionId, definition.Name);
    }

    public WorkflowDefinition? GetDefinition(string definitionId)
    {
        _definitions.TryGetValue(definitionId, out var def);
        return def;
    }

    public IReadOnlyList<WorkflowDefinition> GetDefinitions(WorkflowType? type = null)
    {
        var all = _definitions.Values;
        return type.HasValue
            ? all.Where(d => d.Type == type.Value).ToList()
            : all.ToList();
    }

    public async Task<WorkflowInstance> StartWorkflowAsync(
        string definitionId,
        string? objectId = null,
        string? sourceLocation = null,
        string? targetLocation = null,
        CancellationToken ct = default)
    {
        if (!_definitions.TryGetValue(definitionId, out var definition))
            throw new InvalidOperationException($"Workflow definition '{definitionId}' not found");

        if (!definition.Enabled)
            throw new InvalidOperationException($"Workflow definition '{definitionId}' is disabled");

        var instance = new WorkflowInstance
        {
            DefinitionId = definitionId,
            Type = definition.Type,
            ObjectId = objectId,
            SourceLocation = sourceLocation,
            TargetLocation = targetLocation
        };

        _instances[instance.InstanceId] = instance;
        _logger?.LogInformation("Started workflow instance: {InstanceId} (Definition={DefId}, Type={Type})",
            instance.InstanceId, definitionId, definition.Type);

        // 钩子：流程启动前
        if (_hook != null)
            await _hook.OnWorkflowStartingAsync(instance, ct);

        // 逐个执行阶段
        instance.Status = WorkflowStatus.Running;
        instance.StartTime = DateTime.UtcNow;

        try
        {
            for (int i = 0; i < definition.Stages.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (instance.Status == WorkflowStatus.Paused) break;

                instance.CurrentStageIndex = i;
                var stage = definition.Stages[i];

                // 钩子：阶段启动前
                if (_hook != null)
                    await _hook.OnStageStartingAsync(instance, stage, i, ct);

                var stageResult = await ExecuteStageAsync(stage, instance, ct);
                instance.StageResults.Add(stageResult);

                // 钩子：阶段完成
                if (_hook != null)
                    await _hook.OnStageCompletedAsync(instance, stageResult, ct);

                if (!stageResult.Success)
                {
                    instance.Status = WorkflowStatus.Failed;
                    instance.ErrorMessage = $"Stage '{stage.StageName}' failed: {stageResult.ErrorMessage}";
                    Interlocked.Increment(ref _totalFailed);
                    return instance;
                }
            }

            // 全部完成
            instance.Status = WorkflowStatus.Completed;
            instance.EndTime = DateTime.UtcNow;
            Interlocked.Increment(ref _totalCompleted);

            if (_hook != null)
                await _hook.OnWorkflowCompletedAsync(instance, ct);

            _logger?.LogInformation("Workflow {InstanceId} completed successfully in {Elapsed}ms",
                instance.InstanceId, (instance.EndTime - instance.StartTime)?.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            instance.Status = WorkflowStatus.Cancelled;
            instance.EndTime = DateTime.UtcNow;
            instance.ErrorMessage = "Workflow cancelled";
        }
        catch (Exception ex)
        {
            instance.Status = WorkflowStatus.Failed;
            instance.EndTime = DateTime.UtcNow;
            instance.ErrorMessage = ex.Message;
            Interlocked.Increment(ref _totalFailed);
            _logger?.LogError(ex, "Workflow {InstanceId} failed", instance.InstanceId);
        }

        return instance;
    }

    public WorkflowInstance? GetInstance(string instanceId)
    {
        _instances.TryGetValue(instanceId, out var instance);
        return instance;
    }

    public IEnumerable<WorkflowInstance> GetActiveInstances()
    {
        return _instances.Values
            .Where(i => i.Status == WorkflowStatus.Running || i.Status == WorkflowStatus.Paused)
            .ToList();
    }

    public async Task<bool> CancelWorkflowAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
            return false;

        if (instance.Status != WorkflowStatus.Running && instance.Status != WorkflowStatus.Paused)
            return false;

        instance.Status = WorkflowStatus.Cancelled;
        instance.EndTime = DateTime.UtcNow;
        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> PauseWorkflowAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
            return false;

        if (instance.Status != WorkflowStatus.Running)
            return false;

        instance.Status = WorkflowStatus.Paused;
        await Task.CompletedTask;
        return true;
    }

    public async Task<bool> ResumeWorkflowAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
            return false;

        if (instance.Status != WorkflowStatus.Paused)
            return false;

        instance.Status = WorkflowStatus.Running;
        await Task.CompletedTask;

        // 重新启动执行剩余阶段
        if (_definitions.TryGetValue(instance.DefinitionId, out var def))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = instance.CurrentStageIndex; i < def.Stages.Count; i++)
                    {
                        if (instance.Status != WorkflowStatus.Running) break;
                        var stage = def.Stages[i];
                        var stageResult = await ExecuteStageAsync(stage, instance, ct);
                        instance.StageResults.Add(stageResult);
                        if (!stageResult.Success) break;
                    }

                    if (instance.Status == WorkflowStatus.Running)
                    {
                        instance.Status = WorkflowStatus.Completed;
                        instance.EndTime = DateTime.UtcNow;
                        Interlocked.Increment(ref _totalCompleted);
                    }
                }
                catch (Exception ex)
                {
                    instance.Status = WorkflowStatus.Failed;
                    instance.ErrorMessage = ex.Message;
                }
            }, ct);
        }

        return true;
    }

    public WorkflowCenterStats GetStats()
    {
        var completionTimes = _instances.Values
            .Where(i => i.StartTime.HasValue && i.EndTime.HasValue)
            .Select(i => (i.EndTime.Value - i.StartTime.Value).TotalMilliseconds)
            .ToList();

        return new WorkflowCenterStats
        {
            DefinitionCount = _definitions.Count,
            ActiveInstanceCount = GetActiveInstances().Count(),
            TotalCompleted = (int)Interlocked.Read(ref _totalCompleted),
            TotalFailed = (int)Interlocked.Read(ref _totalFailed),
            AvgCompletionTimeMs = completionTimes.Count > 0 ? completionTimes.Average() : 0
        };
    }

    private async Task<WorkflowStageResult> ExecuteStageAsync(
        WorkflowStage stage, WorkflowInstance instance, CancellationToken ct)
    {
        var result = new WorkflowStageResult
        {
            StageName = stage.StageName,
            StartTime = DateTime.UtcNow,
            TotalTasks = stage.Tasks.Count
        };

        try
        {
            if (stage.Tasks.Count > 0)
            {
                foreach (var task in stage.Tasks)
                {
                    if (ct.IsCancellationRequested) break;
                    task.Tags["WorkflowInstanceId"] = instance.InstanceId;
                    task.Tags["WorkflowStage"] = stage.StageName;
                    await _scheduler.EnqueueAsync(task, ct);
                    result.CompletedTasks++;
                }
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        result.EndTime = DateTime.UtcNow;
        return result;
    }
}
