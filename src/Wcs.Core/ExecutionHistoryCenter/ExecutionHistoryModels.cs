namespace Wcs.Core.ExecutionHistoryCenter;

/// <summary>
/// 运输执行记录 — 单个托盘的完整运输追溯
///
/// 纯 WCS 用途：追溯「这个托盘什么时候来、走过哪些设备、在哪停留、最终去了哪里」
/// 不涉及：订单、批次、库位、FIFO（这些属于 WMS）
/// </summary>
public class TransportExecutionRecord
{
    /// <summary>关联的任务 ID</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>物料/托盘/料箱 ID</summary>
    public string PalletId { get; set; } = string.Empty;

    /// <summary>来源节点 ID</summary>
    public string SourceNode { get; set; } = string.Empty;

    /// <summary>目标节点 ID</summary>
    public string TargetNode { get; set; } = string.Empty;

    /// <summary>实际路径（节点序列）</summary>
    public List<string> Route { get; set; } = new();

    /// <summary>经过的站点记录</summary>
    public List<NodeVisitRecord> NodeVisits { get; set; } = new();

    /// <summary>开始时间</summary>
    public DateTime StartTime { get; set; }

    /// <summary>结束时间</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>是否成功</summary>
    public bool Success { get; set; }

    /// <summary>失败原因</summary>
    public string? FailureReason { get; set; }

    /// <summary>总耗时（毫秒）</summary>
    public long TotalDurationMs => EndTime.HasValue ? (long)(EndTime.Value - StartTime).TotalMilliseconds : 0;
}

/// <summary>
/// 节点访问记录 — 记录物料在每个站点的到达和离开
/// </summary>
public class NodeVisitRecord
{
    /// <summary>节点 ID</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>到达时间</summary>
    public DateTime ArriveTime { get; set; }

    /// <summary>离开时间</summary>
    public DateTime? LeaveTime { get; set; }

    /// <summary>在该节点的停留时间（毫秒）</summary>
    public long DwellTimeMs => LeaveTime.HasValue ? (long)(LeaveTime.Value - ArriveTime).TotalMilliseconds : 0;
}

/// <summary>
/// 执行历史中心接口 — 运输执行记录追溯
/// </summary>
public interface IExecutionHistoryCenter
{
    /// <summary>
    /// 创建运输记录
    /// </summary>
    string CreateRecord(string taskId, string palletId, string sourceNode, string targetNode);

    /// <summary>
    /// 记录到达节点
    /// </summary>
    void RecordNodeArrival(string taskId, string nodeId);

    /// <summary>
    /// 记录离开节点
    /// </summary>
    void RecordNodeDeparture(string taskId, string nodeId);

    /// <summary>
    /// 标记运输完成
    /// </summary>
    void CompleteRecord(string taskId, bool success, string? failureReason = null);

    /// <summary>
    /// 获取运输记录
    /// </summary>
    TransportExecutionRecord? GetRecord(string taskId);

    /// <summary>
    /// 查询指定物料的运输历史
    /// </summary>
    IReadOnlyList<TransportExecutionRecord> GetPalletHistory(string palletId, int maxRecords = 50);

    /// <summary>
    /// 查询经过指定节点的运输记录
    /// </summary>
    IReadOnlyList<TransportExecutionRecord> GetRecordsByNode(string nodeId, int maxRecords = 50);

    /// <summary>
    /// 获取最近运输记录
    /// </summary>
    IReadOnlyList<TransportExecutionRecord> GetRecentRecords(int maxRecords = 100);

    /// <summary>
    /// 获取统计
    /// </summary>
    ExecutionHistoryStats GetStats();

    /// <summary>
    /// 清理超过指定时间的记录
    /// </summary>
    int Cleanup(TimeSpan maxAge);

    /// <summary>
    /// 记录数量
    /// </summary>
    int Count { get; }
}

/// <summary>
/// 执行历史统计
/// </summary>
public class ExecutionHistoryStats
{
    public int TotalRecords { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double AvgDurationMs { get; set; }
    public Dictionary<string, int> NodeTraffic { get; set; } = new();
}
