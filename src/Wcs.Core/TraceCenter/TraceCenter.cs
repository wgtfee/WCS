namespace Wcs.Core.TraceCenter;

/// <summary>
/// 轨迹事件类型
/// </summary>
public enum TraceEventType
{
    TaskCreated = 0,
    TaskScheduled = 1,
    TaskRunning = 2,
    TaskCompleted = 3,
    TaskFailed = 4,
    CommandSent = 10,
    CommandAcked = 11,
    CommandDone = 12,
    CommandTimeout = 13,
    NodeArrived = 20,
    NodeDeparted = 21,
    RouteCalculated = 30,
    RouteBlocked = 31,
    WaitStarted = 40,
    WaitSatisfied = 41,
    WaitTimeout = 42
}

/// <summary>
/// 单条轨迹记录
/// </summary>
public class TraceRecord
{
    /// <summary>轨迹 ID</summary>
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>关联的追踪 ID（TaskId / CommandId / PalletId）</summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>轨迹事件类型</summary>
    public TraceEventType EventType { get; set; }

    /// <summary>事件描述</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>来源模块</summary>
    public string SourceModule { get; set; } = string.Empty;

    /// <summary>关联设备</summary>
    public string? DeviceId { get; set; }

    /// <summary>关联节点</summary>
    public string? NodeId { get; set; }

    /// <summary>上下文数据（JSON）</summary>
    public string? ContextData { get; set; }

    /// <summary>事件时间</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>耗时（从上一步到此步，毫秒）</summary>
    public long ElapsedMs { get; set; }
}

/// <summary>
/// 轨迹中心接口 — 追踪任务/命令/运输的完整执行轨迹
/// 纯 WCS 边界：追踪运输执行过程，不追踪业务流程
/// </summary>
public interface ITraceCenter
{
    /// <summary>
    /// 记录一条轨迹
    /// </summary>
    void Trace(TraceRecord record);

    /// <summary>
    /// 快速记录轨迹
    /// </summary>
    void TraceQuick(string correlationId, TraceEventType eventType, string message,
        string? deviceId = null, string? nodeId = null, string? contextData = null);

    /// <summary>
    /// 查询指定相关 ID 的完整轨迹（按时间排序）
    /// </summary>
    IReadOnlyList<TraceRecord> GetTrace(string correlationId);

    /// <summary>
    /// 查询设备的所有轨迹
    /// </summary>
    IReadOnlyList<TraceRecord> GetDeviceTrace(string deviceId, int maxRecords = 100);

    /// <summary>
    /// 获取最近轨迹
    /// </summary>
    IReadOnlyList<TraceRecord> GetRecentTrace(int maxRecords = 200);

    /// <summary>
    /// 清理超过指定时间的轨迹
    /// </summary>
    int Cleanup(TimeSpan maxAge);
}

/// <summary>
/// 轨迹中心实现 — 纯 WCS 执行轨迹，非业务流程审计
/// </summary>
public class TraceCenter : ITraceCenter
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<TraceRecord>> _traces = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastEventTime = new();
    private const int MaxRecordsPerCorrelation = 500;
    private const int TotalMaxRecords = 50000;
    /// <summary>总记录数计数器（Interlocked 维护），避免每次 Trace 全表求和。</summary>
    private long _totalRecords;

    public void Trace(TraceRecord record)
    {
        var list = _traces.GetOrAdd(record.CorrelationId, _ => new List<TraceRecord>());

        lock (list)
        {
            // 计算与上一步的时间差
            if (_lastEventTime.TryGetValue(record.CorrelationId, out var lastTime))
                record.ElapsedMs = (long)(record.Timestamp - lastTime).TotalMilliseconds;

            list.Add(record);
            _lastEventTime[record.CorrelationId] = record.Timestamp;
            Interlocked.Increment(ref _totalRecords);

            // 限制单条轨迹长度
            if (list.Count > MaxRecordsPerCorrelation)
            {
                var excess = list.Count - MaxRecordsPerCorrelation;
                list.RemoveRange(0, excess);
                Interlocked.Add(ref _totalRecords, -excess);
            }
        }

        // 限制总记录数（O(1) 计数判断，替代全字典 Sum）
        if (Interlocked.Read(ref _totalRecords) > TotalMaxRecords)
            Cleanup(TimeSpan.FromDays(7));
    }

    public void TraceQuick(string correlationId, TraceEventType eventType, string message,
        string? deviceId = null, string? nodeId = null, string? contextData = null)
    {
        Trace(new TraceRecord
        {
            CorrelationId = correlationId,
            EventType = eventType,
            Message = message,
            DeviceId = deviceId,
            NodeId = nodeId,
            ContextData = contextData,
            Timestamp = DateTime.UtcNow
        });
    }

    public IReadOnlyList<TraceRecord> GetTrace(string correlationId)
    {
        if (_traces.TryGetValue(correlationId, out var list))
        {
            lock (list) { return list.OrderBy(r => r.Timestamp).ToList(); }
        }
        return Array.Empty<TraceRecord>();
    }

    public IReadOnlyList<TraceRecord> GetDeviceTrace(string deviceId, int maxRecords = 100)
    {
        return _traces.Values
            .SelectMany(list =>
            {
                lock (list) { return list.Where(r => r.DeviceId == deviceId).ToList(); }
            })
            .OrderByDescending(r => r.Timestamp)
            .Take(maxRecords)
            .ToList();
    }

    public IReadOnlyList<TraceRecord> GetRecentTrace(int maxRecords = 200)
    {
        return _traces.Values
            .SelectMany(list =>
            {
                lock (list) { return list.ToList(); }
            })
            .OrderByDescending(r => r.Timestamp)
            .Take(maxRecords)
            .ToList();
    }

    public int Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge);
        var removed = 0;

        foreach (var kvp in _traces)
        {
            lock (kvp.Value)
            {
                var before = kvp.Value.Count;
                kvp.Value.RemoveAll(r => r.Timestamp < cutoff);
                removed += before - kvp.Value.Count;

                // 空轨迹连同 _lastEventTime 一并清除，防止字典键无限累积。
                if (kvp.Value.Count == 0 && _traces.TryGetValue(kvp.Key, out var current) && ReferenceEquals(current, kvp.Value))
                {
                    _traces.TryRemove(kvp.Key, out _);
                    _lastEventTime.TryRemove(kvp.Key, out _);
                }
            }
        }

        Interlocked.Add(ref _totalRecords, -removed);
        return removed;
    }
}
