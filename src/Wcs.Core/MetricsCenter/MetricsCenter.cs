namespace Wcs.Core.MetricsCenter;

using System.Collections.Concurrent;

/// <summary>
/// 指标中心实现 — 统一收集和查询系统运行指标
///
/// 预定义指标：
/// - task.tps：任务每秒处理量
/// - task.completed：完成任务数
/// - task.failed：失败任务数
/// - task.queue_depth：队列深度
/// - plc.read_latency_ms：PLC 读取延迟
/// - device.active：活跃设备数
/// - device.fault：设备故障数
/// - alarm.active：活跃报警数
/// - command.timeout：命令超时数
/// - route.calculated：路由计算次数
/// </summary>
public class MetricsCenter : IMetricsCenter
{
    private readonly ConcurrentDictionary<string, MetricDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastUpdates = new();
    private readonly object _histogramLock = new();
    private long _totalRecordings;
    private const int MaxHistogramSamples = 1000;

    public void RegisterMetric(MetricDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _definitions[definition.Name] = definition;
    }

    public void Record(string name, double value, Dictionary<string, string>? labels = null)
    {
        Interlocked.Increment(ref _totalRecordings);
        _lastUpdates[name] = DateTime.UtcNow;

        if (!_definitions.TryGetValue(name, out var def))
        {
            // 自动注册为 Gauge
            def = new MetricDefinition { Name = name, Type = MetricType.Gauge };
            _definitions[name] = def;
        }

        switch (def.Type)
        {
            case MetricType.Counter:
                _counters.AddOrUpdate(name, (long)value, (_, old) => old + (long)value);
                break;
            case MetricType.Gauge:
                _gauges[name] = value;
                break;
            case MetricType.Histogram:
                lock (_histogramLock)
                {
                    var samples = _histograms.GetOrAdd(name, _ => new List<double>());
                    samples.Add(value);
                    if (samples.Count > MaxHistogramSamples)
                        samples.RemoveRange(0, samples.Count - MaxHistogramSamples);
                }
                break;
        }
    }

    public void Increment(string name, double delta = 1, Dictionary<string, string>? labels = null)
    {
        Interlocked.Increment(ref _totalRecordings);
        _lastUpdates[name] = DateTime.UtcNow;
        _counters.AddOrUpdate(name, (long)delta, (_, old) => old + (long)delta);

        if (!_definitions.ContainsKey(name))
            _definitions[name] = new MetricDefinition { Name = name, Type = MetricType.Counter };
    }

    public IDisposable MeasureDuration(string name, Dictionary<string, string>? labels = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        return new DurationMeasurement(() =>
        {
            sw.Stop();
            Record(name, sw.Elapsed.TotalMilliseconds, labels);
        });
    }

    public double? GetValue(string name)
    {
        if (_gauges.TryGetValue(name, out var g)) return g;
        if (_counters.TryGetValue(name, out var c)) return c;

        if (_histograms.TryGetValue(name, out var samples))
        {
            lock (_histogramLock)
            {
                return samples.Count > 0 ? samples.Average() : null;
            }
        }
        return null;
    }

    public IReadOnlyList<MetricSnapshot> GetSnapshot()
    {
        var result = new List<MetricSnapshot>();

        foreach (var (name, def) in _definitions)
        {
            var snap = new MetricSnapshot
            {
                Name = name,
                Type = def.Type,
                Unit = def.Unit,
                LastUpdate = _lastUpdates.TryGetValue(name, out var t) ? t : DateTime.MinValue
            };

            switch (def.Type)
            {
                case MetricType.Counter:
                    snap.Value = _counters.TryGetValue(name, out var c) ? c : 0;
                    snap.Count = (long)snap.Value;
                    break;

                case MetricType.Gauge:
                    snap.Value = _gauges.TryGetValue(name, out var g) ? g : 0;
                    break;

                case MetricType.Histogram:
                    if (_histograms.TryGetValue(name, out var samples))
                    {
                        lock (_histogramLock)
                        {
                            snap.Count = samples.Count;
                            if (samples.Count > 0)
                            {
                                snap.Min = samples.Min();
                                snap.Max = samples.Max();
                                snap.Avg = samples.Average();
                                snap.Value = snap.Avg.Value;
                            }
                        }
                    }
                    break;
            }

            result.Add(snap);
        }

        return result;
    }

    public void Reset()
    {
        _gauges.Clear();
        _counters.Clear();
        _histograms.Clear();
        _lastUpdates.Clear();
    }

    public MetricsStats GetStats()
    {
        return new MetricsStats
        {
            RegisteredMetrics = _definitions.Count,
            TotalRecordings = Interlocked.Read(ref _totalRecordings)
        };
    }

    /// <summary>
    /// 常用预注册指标
    /// </summary>
    public void RegisterDefaultMetrics()
    {
        RegisterMetric(new() { Name = "task.tps", Type = MetricType.Gauge, Description = "Tasks per second" });
        RegisterMetric(new() { Name = "task.completed", Type = MetricType.Counter, Description = "Total completed tasks" });
        RegisterMetric(new() { Name = "task.failed", Type = MetricType.Counter, Description = "Total failed tasks" });
        RegisterMetric(new() { Name = "task.queue_depth", Type = MetricType.Gauge, Description = "Current queue depth" });
        RegisterMetric(new() { Name = "plc.read_latency_ms", Type = MetricType.Histogram, Description = "PLC read latency", Unit = "ms" });
        RegisterMetric(new() { Name = "alarm.active", Type = MetricType.Gauge, Description = "Active alarms" });
        RegisterMetric(new() { Name = "device.active", Type = MetricType.Gauge, Description = "Active devices" });
        RegisterMetric(new() { Name = "command.timeout", Type = MetricType.Counter, Description = "Command timeouts" });
        RegisterMetric(new() { Name = "route.calculated", Type = MetricType.Counter, Description = "Route calculations" });
    }

    private sealed class DurationMeasurement : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;
        public DurationMeasurement(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}
