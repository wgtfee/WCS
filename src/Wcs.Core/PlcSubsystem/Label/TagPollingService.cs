using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 标签轮询服务 — 注册 [PlcStruct] 类型后定时读取标签数据
///
/// 对标 S7PollingService 的模式：
///   - S7PollingService 从 PlcStructRegistry 读取所有 DB 块注册
///   - TagPollingService 从内部列表读取所有 [PlcStruct] 类型注册
///
/// 注册方式：
///   1. 代码注册：service.AddPoll{ConveyorStatus}()
///   2. JSON 配置：PlcTagPolls → AddFromConfig()
///
/// 轮询间隔从 [PlcStruct(RefreshRateMs)] 特性读取。
/// </summary>
public class TagPollingService : IDisposable
{
    private readonly PlcTagSerializer _serializer;
    private readonly ILogger<TagPollingService>? _logger;
    private readonly List<TagPollRegistration> _registrations = new();
    private readonly List<Timer> _timers = new();
    private bool _running;

    public TagPollingService(
        PlcTagSerializer serializer,
        ILogger<TagPollingService>? logger = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger;
    }

    /// <summary>从 JSON 配置注册轮询（PlcTagPolls）</summary>
    public void AddFromConfig(IEnumerable<TagPollConfig> configs)
    {
        foreach (var cfg in configs)
        {
            var type = Type.GetType(cfg.StructType);
            if (type == null)
            {
                _logger?.LogWarning("[TagPoll] 找不到类型 '{Type}'", cfg.StructType);
                continue;
            }
            AddPoll(type);
        }
    }

    /// <summary>代码注册轮询</summary>
    public void AddPoll<T>() where T : class, new() => AddPoll(typeof(T));

    /// <summary>代码注册轮询</summary>
    public void AddPoll(Type type)
    {
        var structAttr = type.GetCustomAttribute<PlcStructAttribute>();
        if (structAttr == null)
        {
            _logger?.LogWarning("[TagPoll] 类型 '{Type}' 缺少 [PlcStruct] 特性", type.Name);
            return;
        }

        _registrations.Add(new TagPollRegistration
        {
            StructType = type,
            PollIntervalMs = structAttr.RefreshRateMs
        });

        _logger?.LogInformation("[TagPoll] 注册 {Type} (间隔 {Interval}ms)", type.Name, structAttr.RefreshRateMs);
    }

    /// <summary>启动所有轮询任务</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;

        foreach (var reg in _registrations)
        {
            var timer = new Timer(async _ =>
            {
                try
                {
                    var instance = Activator.CreateInstance(reg.StructType);
                    if (instance == null) return;

                    await _serializer.ReadAsync(instance);

                    foreach (var prop in reg.StructType.GetProperties(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (prop.GetCustomAttribute<PlcIgnoreAttribute>() != null) continue;
                        var tagAttr = prop.GetCustomAttribute<PlcTagAttribute>();
                        if (tagAttr == null || !tagAttr.Monitored) continue;

                        _logger?.LogDebug("[TagPoll] {Tag} = {Value}", tagAttr.Name, prop.GetValue(instance));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[TagPoll] {Type}", reg.StructType.Name);
                }
            }, null, 0, reg.PollIntervalMs);

            _timers.Add(timer);
        }

        _logger?.LogInformation("[TagPoll] 启动完成，共 {Count} 个类型", _registrations.Count);
    }

    /// <summary>停止所有轮询任务</summary>
    public void Stop()
    {
        _running = false;
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    public class TagPollRegistration
    {
        public Type StructType { get; init; } = null!;
        public int PollIntervalMs { get; init; } = 1000;
    }
}
