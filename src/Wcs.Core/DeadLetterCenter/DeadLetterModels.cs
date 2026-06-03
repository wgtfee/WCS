namespace Wcs.Core.DeadLetterCenter;

/// <summary>
/// 死信类型
/// </summary>
public enum DeadLetterType
{
    /// <summary>任务生成失败</summary>
    TaskGenerationFailed = 0,
    /// <summary>任务执行失败</summary>
    TaskExecutionFailed = 1,
    /// <summary>命令超时</summary>
    CommandTimeout = 2,
    /// <summary>命令被拒绝</summary>
    CommandRejected = 3,
    /// <summary>设备故障</summary>
    DeviceFault = 4,
    /// <summary>路由规划失败</summary>
    RouteFailed = 5,
    /// <summary>规则匹配异常</summary>
    RuleEngineException = 6,
    /// <summary>未处理的异常</summary>
    UnhandledException = 99
}

/// <summary>
/// 死信记录 — 描述一个失败的任务/命令/操作的详细信息
/// </summary>
public class DeadLetterRecord
{
    /// <summary>唯一 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>死信类型</summary>
    public DeadLetterType Type { get; set; }

    /// <summary>来源模块</summary>
    public string SourceModule { get; set; } = string.Empty;

    /// <summary>关联的原始 ID（任务 ID/命令 ID/规则 ID）</summary>
    public string? OriginalId { get; set; }

    /// <summary>关联的设备 ID</summary>
    public string? DeviceId { get; set; }

    /// <summary>简要描述</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>详细错误信息</summary>
    public string? Detail { get; set; }

    /// <summary>异常堆栈</summary>
    public string? StackTrace { get; set; }

    /// <summary>上下文数据（JSON）</summary>
    public string? ContextData { get; set; }

    /// <summary>是否已处理（人工介入后标记）</summary>
    public bool IsResolved { get; set; }

    /// <summary>处理人/处理说明</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>处理时间</summary>
    public DateTime? ResolvedTime { get; set; }
}

/// <summary>
/// 死信中心接口 — 统一管理所有失败记录，替代直接 throw/log 后无人追踪
/// </summary>
public interface IDeadLetterCenter
{
    /// <summary>
    /// 投递一条死信
    /// </summary>
    string Post(DeadLetterRecord record);

    /// <summary>
    /// 快速投递死信（简化参数）
    /// </summary>
    string PostQuick(DeadLetterType type, string sourceModule, string summary,
        string? originalId = null, string? deviceId = null, string? detail = null);

    /// <summary>
    /// 获取死信
    /// </summary>
    DeadLetterRecord? Get(string id);

    /// <summary>
    /// 查询死信（按类型过滤）
    /// </summary>
    IEnumerable<DeadLetterRecord> Query(DeadLetterType? type = null,
        string? sourceModule = null, string? deviceId = null, int maxResults = 100);

    /// <summary>
    /// 标记死信已处理
    /// </summary>
    bool Resolve(string id, string resolvedBy);

    /// <summary>
    /// 获取未处理的死信数
    /// </summary>
    int GetUnresolvedCount();

    /// <summary>
    /// 获取统计
    /// </summary>
    DeadLetterStats GetStats();

    /// <summary>
    /// 清理已处理的死信（超过指定时间）
    /// </summary>
    int Cleanup(TimeSpan maxAge);
}

/// <summary>
/// 死信中心统计
/// </summary>
public class DeadLetterStats
{
    public int TotalRecords { get; set; }
    public int UnresolvedCount { get; set; }
    public int TaskFailures { get; set; }
    public int CommandTimeouts { get; set; }
    public int RouteFailures { get; set; }
    public int DeviceFaults { get; set; }
}
