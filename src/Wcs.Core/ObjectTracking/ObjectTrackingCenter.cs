namespace Wcs.Core.ObjectTracking;

using System.Collections.Concurrent;
using System.Text.Json;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.ObjectTracking.Models;
using Wcs.Core.ObjectTracking.Topology;
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

    // ========== 预占位管理 ==========

    /// <summary>
    /// 预约指定节点 — 防止双托盘占位
    /// </summary>
    bool ReservePosition(string objectId, string nodeId, List<string>? route = null);

    /// <summary>
    /// 确认到达预约节点（到达后确认，释放预占）
    /// </summary>
    bool ConfirmPosition(string objectId, string currentNodeId);

    /// <summary>
    /// 取消预约
    /// </summary>
    bool CancelReservation(string objectId);

    /// <summary>
    /// 获取物料预约的节点
    /// </summary>
    string? GetReservedNode(string objectId);
}

/// <summary>
/// 物料跟踪中心实现
/// 新增功能：移动历史索引、Zone 空间索引、Task 索引、事件发布
/// </summary>
public class ObjectTrackingCenter : IObjectTrackingCenter, ISnapshotProvider
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

    /// <summary>
    /// 可选的拓扑图引用，用于路径查询和空间推理。
    /// </summary>
    public TopologyGraph? TopologyGraph { get; private set; }

    /// <summary>
    /// 设置拓扑图引用。
    /// </summary>
    public void SetTopologyGraph(TopologyGraph graph)
    {
        TopologyGraph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

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

    /// <summary>
    /// 拓扑感知查询：获取在指定路径上的所有物料。
    /// 使用 TopologyGraph 查找从 fromNodeId 到 toNodeId 的最短路径，
    /// 然后返回位置落在路径节点上的所有物料。
    /// </summary>
    public IEnumerable<ObjectState> GetObjectsOnPath(string fromNodeId, string toNodeId)
    {
        if (TopologyGraph == null)
            return Enumerable.Empty<ObjectState>();

        var path = TopologyGraph.GetShortestPath(fromNodeId, toNodeId);
        if (!path.Found || path.NodePath.Count == 0)
            return Enumerable.Empty<ObjectState>();

        // 收集路径上所有节点的位置键
        var pathPositionKeys = new HashSet<string>();
        foreach (var nodeId in path.NodePath)
        {
            var node = TopologyGraph.GetNode(nodeId);
            if (node == null)
                continue;

            // 使用 Node 中的 ZoneId/ConveyorId/PositionId 构建位置键
            var positionKey = $"{node.ZoneId}.{node.ConveyorId}.{node.PositionId}";
            if (!string.IsNullOrWhiteSpace(node.ZoneId))
                pathPositionKeys.Add(positionKey);
        }

        if (pathPositionKeys.Count == 0)
            return Enumerable.Empty<ObjectState>();

        // 返回位置匹配的物料
        return _objects.Values
            .Where(o => !string.IsNullOrEmpty(o.CurrentPosition) &&
                        pathPositionKeys.Contains(o.CurrentPosition))
            .ToList();
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

    // ========== 预占位管理 ==========

    /// <summary>
    /// 预约指定节点 — 检查是否已被其他物料预约，没有则成功
    /// 需要 TopologyGraph 配合检查节点占用
    /// </summary>
    public bool ReservePosition(string objectId, string nodeId, List<string>? route = null)
    {
        if (!_objects.TryGetValue(objectId, out var state))
            return false;

        // 检查节点是否已被其他物料预约
        foreach (var obj in _objects.Values)
        {
            if (obj.ObjectId != objectId &&
                obj.ReservedNodeId == nodeId &&
                obj.Status != ObjectStatusEnum.Completed)
            {
                return false; // 节点已被其他物料预约
            }
        }

        // 检查 TopologyGraph 中该节点是否已被占用
        if (TopologyGraph != null && TopologyGraph.IsNodeReserved(nodeId))
        {
            return false;
        }

        state.ReservedNodeId = nodeId;
        state.Route = route;
        state.UpdateTime = DateTime.UtcNow;

        // 在 TopologyGraph 中标记预约
        TopologyGraph?.SetNodeOccupied(nodeId, true);
        TopologyGraph?.SetNodeOccupiedBy(nodeId, objectId);

        return true;
    }

    /// <summary>
    /// 确认到达预约节点
    /// </summary>
    public bool ConfirmPosition(string objectId, string currentNodeId)
    {
        if (!_objects.TryGetValue(objectId, out var state))
            return false;

        if (state.ReservedNodeId == null)
            return false;

        // 清理旧预约标记
        var oldReserved = state.ReservedNodeId;
        TopologyGraph?.SetNodeOccupied(oldReserved, false);
        TopologyGraph?.SetNodeOccupiedBy(oldReserved, null);

        // 更新当前位置
        state.CurrentPosition = currentNodeId;
        state.ReservedNodeId = null;
        state.Route = null;
        state.UpdateTime = DateTime.UtcNow;

        // 如果新位置在 TopologyGraph 中有对应节点，标记为占用
        TopologyGraph?.SetNodeOccupied(currentNodeId, true);
        TopologyGraph?.SetNodeOccupiedBy(currentNodeId, objectId);

        return true;
    }

    /// <summary>
    /// 取消预约
    /// </summary>
    public bool CancelReservation(string objectId)
    {
        if (!_objects.TryGetValue(objectId, out var state))
            return false;

        if (state.ReservedNodeId == null)
            return false;

        var reservedNode = state.ReservedNodeId;
        state.ReservedNodeId = null;
        state.Route = null;
        state.UpdateTime = DateTime.UtcNow;

        // 释放 TopologyGraph 中的预约
        TopologyGraph?.SetNodeOccupied(reservedNode, false);
        TopologyGraph?.SetNodeOccupiedBy(reservedNode, null);

        return true;
    }

    /// <summary>
    /// 获取物料预约的节点
    /// </summary>
    public string? GetReservedNode(string objectId)
    {
        return _objects.TryGetValue(objectId, out var state) ? state.ReservedNodeId : null;
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

    // ==================== ISnapshotProvider ====================

    public string ModuleName => "ObjectTracking";
    public int RestoreOrder => 1;

    public Task<object> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        return Task.FromResult<object>(GetSnapshot());
    }

    public Task RestoreSnapshotAsync(object snapshot, CancellationToken ct = default)
    {
        if (snapshot is JsonElement element)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, ObjectState>>(element.GetRawText());
            if (dict != null) RestoreFromSnapshot(dict);
        }
        else if (snapshot is Dictionary<string, ObjectState> dict)
        {
            RestoreFromSnapshot(dict);
        }
        return Task.CompletedTask;
    }

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
