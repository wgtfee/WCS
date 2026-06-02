namespace Wcs.Core.ObjectTracking;

using System.Collections.Concurrent;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.ObjectTracking.Models;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 物料跟踪中心接口 - 数字孪生核心
/// </summary>
public interface IObjectTrackingCenter
{
    /// <summary>
    /// 注册/更新物料
    /// </summary>
    void TrackObject(string objectId, string position, string? targetPosition = null,
        ObjectStatusEnum status = ObjectStatusEnum.Idle, string? taskId = null);

    /// <summary>
    /// 移动物料到新位置
    /// </summary>
    void MoveObject(string objectId, string newPosition, string? taskId = null);

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
    /// 获取指定区域的所有物料
    /// </summary>
    IEnumerable<ObjectState> GetObjectsByZone(string zone);

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
    /// 获取物料移动历史
    /// </summary>
    IReadOnlyList<MovementRecord> GetMovementHistory(string objectId, int maxRecords = 100);

    /// <summary>
    /// 获取任务关联的物体 ID
    /// </summary>
    string? GetObjectByTask(string taskId);

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
/// 新增功能：移动历史索引、Zone 空间索引、Task 索引、事件发布
/// </summary>
public class ObjectTrackingCenter : IObjectTrackingCenter
{
    private readonly ConcurrentDictionary<string, ObjectState> _objects = new();

    // 移动历史索引：objectId → 移动记录列表
    private readonly ConcurrentDictionary<string, List<MovementRecord>> _movementHistory = new();

    // 空间索引：zoneKey → objectIds
    private readonly ConcurrentDictionary<string, HashSet<string>> _spatialIndex = new();

    // 任务索引：taskId → objectId
    private readonly ConcurrentDictionary<string, string> _taskIndex = new();

    private readonly IEventBus? _eventBus;

    /// <summary>
    /// 移动历史保留上限（每种物体）
    /// </summary>
    private const int MaxHistoryPerObject = 1000;

    public ObjectTrackingCenter(IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    public void TrackObject(string objectId, string position, string? targetPosition = null,
        ObjectStatusEnum status = ObjectStatusEnum.Idle, string? taskId = null)
    {
        var state = new ObjectState
        {
            ObjectId = objectId,
            CurrentPosition = position,
            TargetPosition = targetPosition,
            Status = status,
            UpdateTime = DateTime.UtcNow
        };

        bool isNew = _objects.TryAdd(objectId, state);
        if (!isNew)
            _objects[objectId] = state;

        // 更新空间索引
        UpdateSpatialIndex(objectId, null, position);

        // 更新任务索引
        if (!string.IsNullOrEmpty(taskId))
            _taskIndex[taskId] = objectId;

        // 记录进入事件
        var location = Location.FromString(position);
        var record = new MovementRecord
        {
            ObjectId = objectId,
            To = location,
            MoveTime = DateTime.UtcNow,
            TriggeredByTaskId = taskId,
            Type = isNew ? MovementType.Entered : MovementType.Transferred
        };
        AddMovementRecord(objectId, record);

        // 发布事件
        _eventBus?.PublishAsync(new ObjectLocationChangedEvent
        {
            ObjectId = objectId,
            NewPosition = position,
            TargetPosition = targetPosition
        });
    }

    public void MoveObject(string objectId, string newPosition, string? taskId = null)
    {
        if (!_objects.TryGetValue(objectId, out var state))
            return;

        var oldPosition = state.CurrentPosition;
        state.CurrentPosition = newPosition;
        state.UpdateTime = DateTime.UtcNow;

        // 更新空间索引
        UpdateSpatialIndex(objectId, oldPosition, newPosition);

        // 记录移动事件
        var fromLoc = Location.FromString(oldPosition);
        var toLoc = Location.FromString(newPosition);
        var record = new MovementRecord
        {
            ObjectId = objectId,
            From = fromLoc,
            To = toLoc,
            MoveTime = DateTime.UtcNow,
            TriggeredByTaskId = taskId,
            Type = MovementType.Moved
        };
        AddMovementRecord(objectId, record);

        // 发布事件
        _eventBus?.PublishAsync(new ObjectLocationChangedEvent
        {
            ObjectId = objectId,
            OldPosition = oldPosition,
            NewPosition = newPosition,
            TargetPosition = state.TargetPosition
        });
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

    public IEnumerable<ObjectState> GetObjectsByZone(string zone)
    {
        if (!_spatialIndex.TryGetValue(zone, out var objectIds))
            return Enumerable.Empty<ObjectState>();

        lock (objectIds)
        {
            return objectIds
                .Select(id => _objects.TryGetValue(id, out var s) ? s : null)
                .Where(s => s != null)
                .ToList()!;
        }
    }

    public IEnumerable<ObjectState> GetMovingObjects()
    {
        return _objects.Values.Where(o => o.Status == ObjectStatusEnum.Moving).ToList();
    }

    public void RemoveObject(string objectId)
    {
        if (!_objects.TryRemove(objectId, out var state))
            return;

        // 清理空间索引
        var location = Location.FromString(state.CurrentPosition);
        RemoveFromSpatialIndex(objectId, location.ZoneKey);
        RemoveFromSpatialIndex(objectId, location.ConveyorKey);

        // 清理任务索引
        var taskEntry = _taskIndex.FirstOrDefault(kvp => kvp.Value == objectId);
        if (!string.IsNullOrEmpty(taskEntry.Key))
            _taskIndex.TryRemove(taskEntry.Key, out _);

        // 记录离开事件
        var record = new MovementRecord
        {
            ObjectId = objectId,
            From = location,
            MoveTime = DateTime.UtcNow,
            Type = MovementType.Exited
        };
        AddMovementRecord(objectId, record);
    }

    public bool Exists(string objectId)
    {
        return _objects.ContainsKey(objectId);
    }

    public IReadOnlyList<MovementRecord> GetMovementHistory(string objectId, int maxRecords = 100)
    {
        if (!_movementHistory.TryGetValue(objectId, out var records))
            return Array.Empty<MovementRecord>();

        lock (records)
        {
            return records.TakeLast(maxRecords).ToList().AsReadOnly();
        }
    }

    public string? GetObjectByTask(string taskId)
    {
        return _taskIndex.TryGetValue(taskId, out var objectId) ? objectId : null;
    }

    public Dictionary<string, ObjectState> GetSnapshot()
    {
        return new Dictionary<string, ObjectState>(_objects);
    }

    public void RestoreFromSnapshot(Dictionary<string, ObjectState> snapshot)
    {
        _objects.Clear();
        _movementHistory.Clear();
        _spatialIndex.Clear();
        _taskIndex.Clear();

        foreach (var kvp in snapshot)
        {
            _objects[kvp.Key] = kvp.Value;
            UpdateSpatialIndex(kvp.Key, null, kvp.Value.CurrentPosition);
        }
    }

    public int Count => _objects.Count;

    // ==================== 内部方法 ====================

    private void AddMovementRecord(string objectId, MovementRecord record)
    {
        var records = _movementHistory.GetOrAdd(objectId, _ => new List<MovementRecord>());

        lock (records)
        {
            records.Add(record);
            // 超过上限裁剪
            if (records.Count > MaxHistoryPerObject)
            {
                records.RemoveRange(0, records.Count - MaxHistoryPerObject);
            }
        }
    }

    private void UpdateSpatialIndex(string objectId, string? oldPosition, string newPosition)
    {
        // 从旧位置移除
        if (!string.IsNullOrEmpty(oldPosition))
        {
            var oldLoc = Location.FromString(oldPosition);
            RemoveFromSpatialIndex(objectId, oldLoc.ZoneKey);
            RemoveFromSpatialIndex(objectId, oldLoc.ConveyorKey);
        }

        // 加入新位置
        var newLoc = Location.FromString(newPosition);
        AddToSpatialIndex(objectId, newLoc.ZoneKey);
        AddToSpatialIndex(objectId, newLoc.ConveyorKey);
    }

    private void AddToSpatialIndex(string objectId, string key)
    {
        var ids = _spatialIndex.GetOrAdd(key, _ => new HashSet<string>());
        lock (ids)
        {
            ids.Add(objectId);
        }
    }

    private void RemoveFromSpatialIndex(string objectId, string key)
    {
        if (_spatialIndex.TryGetValue(key, out var ids))
        {
            lock (ids)
            {
                ids.Remove(objectId);
            }
        }
    }
}
