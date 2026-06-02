namespace Wcs.Core.StateMachine;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// 设备状态机 - 定义 DeviceStatusEnum 的合法转移规则
/// </summary>
public class DeviceStateMachine : IDeviceStateMachine
{
    private DeviceStatusEnum _current;
    private readonly List<Action<DeviceStatusEnum, DeviceStatusEnum>> _callbacks = new();
    private readonly object _lock = new();

    private static readonly HashSet<(DeviceStatusEnum, DeviceStatusEnum)> ValidTransitions = new()
    {
        (DeviceStatusEnum.Offline, DeviceStatusEnum.Online),
        (DeviceStatusEnum.Offline, DeviceStatusEnum.Error),

        (DeviceStatusEnum.Online, DeviceStatusEnum.Idle),
        (DeviceStatusEnum.Online, DeviceStatusEnum.Offline),

        (DeviceStatusEnum.Idle, DeviceStatusEnum.Running),
        (DeviceStatusEnum.Idle, DeviceStatusEnum.Offline),
        (DeviceStatusEnum.Idle, DeviceStatusEnum.Maintenance),

        (DeviceStatusEnum.Running, DeviceStatusEnum.Idle),
        (DeviceStatusEnum.Running, DeviceStatusEnum.Error),
        (DeviceStatusEnum.Running, DeviceStatusEnum.Offline),

        (DeviceStatusEnum.Error, DeviceStatusEnum.Idle),
        (DeviceStatusEnum.Error, DeviceStatusEnum.Maintenance),
        (DeviceStatusEnum.Error, DeviceStatusEnum.Offline),

        (DeviceStatusEnum.Maintenance, DeviceStatusEnum.Idle),
        (DeviceStatusEnum.Maintenance, DeviceStatusEnum.Offline),
    };

    public DeviceStatusEnum CurrentState
    {
        get { lock (_lock) return _current; }
    }

    public DeviceStateMachine(DeviceStatusEnum initialState = DeviceStatusEnum.Offline)
    {
        _current = initialState;
    }

    public bool TryTransitionTo(DeviceStatusEnum target, out string? reason)
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
                reason = $"设备状态不允许从 {_current} 转移到 {target}";
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

    public bool CanTransitionTo(DeviceStatusEnum target)
    {
        lock (_lock)
        {
            return _current == target || ValidTransitions.Contains((_current, target));
        }
    }

    public IEnumerable<DeviceStatusEnum> GetAllowedTransitions()
    {
        lock (_lock)
        {
            return ValidTransitions
                .Where(t => t.Item1 == _current)
                .Select(t => t.Item2)
                .ToList();
        }
    }

    public void OnTransition(Action<DeviceStatusEnum, DeviceStatusEnum> callback)
    {
        lock (_lock)
        {
            _callbacks.Add(callback);
        }
    }

    public void Reset(DeviceStatusEnum state)
    {
        lock (_lock)
        {
            _current = state;
        }
    }
}
