namespace Wcs.Core.MetricsCenter;

/// <summary>
/// 指标类型
/// </summary>
public enum MetricType
{
    /// <summary>累计计数器</summary>
    Counter = 0,
    /// <summary>瞬时值（Gauge）</summary>
    Gauge = 1,
    /// <summary>直方图（耗时分布）</summary>
    Histogram = 2
}

/// <summary>
/// 单个指标点
/// </summary>
public class MetricPoint
{
    public string Name { get; set; } = string.Empty;
    public double Value { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>
/// 指标定义
/// </summary>
public class MetricDefinition
{
    public string Name { get; set; } = string.Empty;
    public MetricType Type { get; set; }
    public string? Description { get; set; }
    public string? Unit { get; set; }
}

/// <summary>
/// 指标中心接口 — 统一收集和查询系统运行指标
/// 数据可用于 Prometheus/Grafana 监控面板
/// </summary>
public interface IMetricsCenter
{
    /// <summary>
    /// 注册指标定义
    /// </summary>
    void RegisterMetric(MetricDefinition definition);

    /// <summary>
    /// 记录指标值
    /// </summary>
    void Record(string name, double value, Dictionary<string, string>? labels = null);

    /// <summary>
    /// 递增计数器
    /// </summary>
    void Increment(string name, double delta = 1, Dictionary<string, string>? labels = null);

    /// <summary>
    /// 记录耗时（自动计算开始到现在的毫秒数）
    /// </summary>
    IDisposable MeasureDuration(string name, Dictionary<string, string>? labels = null);

    /// <summary>
    /// 获取指标当前值
    /// </summary>
    double? GetValue(string name);

    /// <summary>
    /// 获取所有指标快照
    /// </summary>
    IReadOnlyList<MetricSnapshot> GetSnapshot();

    /// <summary>
    /// 重置所有指标
    /// </summary>
    void Reset();

    /// <summary>
    /// 指标中心统计
    /// </summary>
    MetricsStats GetStats();
}

/// <summary>
/// 指标快照（供监控面板查询）
/// </summary>
public class MetricSnapshot
{
    public string Name { get; set; } = string.Empty;
    public MetricType Type { get; set; }
    public double Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Avg { get; set; }
    public long? Count { get; set; }
    public string? Unit { get; set; }
    public DateTime LastUpdate { get; set; }
}

/// <summary>
/// 指标中心统计
/// </summary>
public class MetricsStats
{
    public int RegisteredMetrics { get; set; }
    public long TotalRecordings { get; set; }
}
