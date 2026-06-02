namespace Wcs.Core.StateMachine;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// 任务状态机 - 定义 TaskStatusEnum 的合法转移规则
/// </summary>
public class TaskStateMachine : ITaskStateMachine
{
    private TaskStatusEnum _current;
    private readonly List<Action<TaskStatusEnum, TaskStatusEnum>> _callbacks = new();
    private readonly object _lock = new();

    private static readonly HashSet<(TaskStatusEnum, TaskStatusEnum)> ValidTransitions = new()
    {
        (TaskStatusEnum.Created, TaskStatusEnum.Queued),
        (TaskStatusEnum.Created, TaskStatusEnum.Cancelled),

        (TaskStatusEnum.Queued, TaskStatusEnum.Running),
        (TaskStatusEnum.Queued, TaskStatusEnum.Cancelled),

        (TaskStatusEnum.Running, TaskStatusEnum.Completed),
        (TaskStatusEnum.Running, TaskStatusEnum.Failed),
        (TaskStatusEnum.Running, TaskStatusEnum.Paused),
        (TaskStatusEnum.Running, TaskStatusEnum.Cancelled),

        (TaskStatusEnum.Paused, TaskStatusEnum.Running),
        (TaskStatusEnum.Paused, TaskStatusEnum.Cancelled),

        (TaskStatusEnum.Failed, TaskStatusEnum.Running),
        (TaskStatusEnum.Failed, TaskStatusEnum.Recovered),

        (TaskStatusEnum.Recovered, TaskStatusEnum.Queued),
        (TaskStatusEnum.Recovered, TaskStatusEnum.Running),
    };

    public TaskStatusEnum CurrentState
    {
        get { lock (_lock) return _current; }
    }

    public TaskStateMachine(TaskStatusEnum initialState = TaskStatusEnum.Created)
    {
        _current = initialState;
    }

    public bool TryTransitionTo(TaskStatusEnum target, out string? reason)
    {
        lock (_lock)
        {
            if (_current == target)
            {
                reason = null;
                return true;
            }

            if (!ValidTransitions.Contains((_current, target)))
            {
                reason = $"任务状态不允许从 {_current} 转移到 {target}";
                return false;
            }

            var old = _current;
            _current = target;

            foreach (var cb in _callbacks)
            {
                try { cb(old, target); } catch { }
            }

            reason = null;
            return true;
        }
    }

    public bool CanTransitionTo(TaskStatusEnum target)
    {
        lock (_lock)
        {
            return _current == target || ValidTransitions.Contains((_current, target));
        }
    }

    public IEnumerable<TaskStatusEnum> GetAllowedTransitions()
    {
        lock (_lock)
        {
            return ValidTransitions
                .Where(t => t.Item1 == _current)
                .Select(t => t.Item2)
                .ToList();
        }
    }

    public void OnTransition(Action<TaskStatusEnum, TaskStatusEnum> callback)
    {
        lock (_lock)
        {
            _callbacks.Add(callback);
        }
    }

    public void Reset(TaskStatusEnum state)
    {
        lock (_lock)
        {
            _current = state;
        }
    }
}
