namespace Wcs.Core.ObjectTracking;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// 物料跟踪中心接口 - 数字孪生核心
/// </summary>
public interface IObjectTrackingCenter
{
    /// <summary>
    /// 注册/更新物料
    /// </summary>
    void TrackObject(string objectId, string position, string? targetPosition = null, ObjectStatusEnum status = ObjectStatusEnum.Idle);

    /// <summary>
    /// 移动物料到新位置
    /// </summary>
    void MoveObject(string objectId, string newPosition);

    /// <summary>
    /// 获取物料当前位置
    /// </summary>
    string? GetObjectPosition(string objectId);

    /// <summary>
    /// 获取物料完整状态
    /// </summary>
    ObjectState? GetObject(string objectId);

    /// <summary>
    /// 获取指定位置的所有物料
    /// </summary>
    IEnumerable<ObjectState> GetObjectsAtPosition(string position);

    /// <summary>
    /// 获取所有在途物料
    /// </summary>
    IEnumerable<ObjectState> GetMovingObjects();

    /// <summary>
    /// 移除物料
    /// </summary>
    void RemoveObject(string objectId);

    /// <summary>
    /// 存在性检查
    /// </summary>
    bool Exists(string objectId);

    /// <summary>
    /// 获取全部物料快照
    /// </summary>
    Dictionary<string, ObjectState> GetSnapshot();

    /// <summary>
    /// 从快照恢复
    /// </summary>
    void RestoreFromSnapshot(Dictionary<string, ObjectState> snapshot);

    /// <summary>
    /// 获取物料总数
    /// </summary>
    int Count { get; }
}

/// <summary>
/// 物料跟踪中心实现
/// </summary>
public class ObjectTrackingCenter : IObjectTrackingCenter
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ObjectState> _objects = new();

    public void TrackObject(string objectId, string position, string? targetPosition = null, ObjectStatusEnum status = ObjectStatusEnum.Idle)
    {
        var state = new ObjectState
        {
            ObjectId = objectId,
            CurrentPosition = position,
            TargetPosition = targetPosition,
            Status = status,
            UpdateTime = DateTime.UtcNow
        };
        _objects[objectId] = state;
    }

    public void MoveObject(string objectId, string newPosition)
    {
        if (_objects.TryGetValue(objectId, out var state))
        {
            state.CurrentPosition = newPosition;
            state.UpdateTime = DateTime.UtcNow;
        }
    }

    public string? GetObjectPosition(string objectId)
    {
        return _objects.TryGetValue(objectId, out var state) ? state.CurrentPosition : null;
    }

    public ObjectState? GetObject(string objectId)
    {
        _objects.TryGetValue(objectId, out var state);
        return state;
    }

    public IEnumerable<ObjectState> GetObjectsAtPosition(string position)
    {
        return _objects.Values.Where(o => o.CurrentPosition == position).ToList();
    }

    public IEnumerable<ObjectState> GetMovingObjects()
    {
        return _objects.Values.Where(o => o.Status == ObjectStatusEnum.Moving).ToList();
    }

    public void RemoveObject(string objectId)
    {
        _objects.TryRemove(objectId, out _);
    }

    public bool Exists(string objectId)
    {
        return _objects.ContainsKey(objectId);
    }

    public Dictionary<string, ObjectState> GetSnapshot()
    {
        return new Dictionary<string, ObjectState>(_objects);
    }

    public void RestoreFromSnapshot(Dictionary<string, ObjectState> snapshot)
    {
        _objects.Clear();
        foreach (var kvp in snapshot)
        {
            _objects[kvp.Key] = kvp.Value;
        }
    }

    public int Count => _objects.Count;
}
