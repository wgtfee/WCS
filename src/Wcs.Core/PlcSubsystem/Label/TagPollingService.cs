using System.Reflection;
using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 标签轮询服务 — 按 [PlcStruct] 注册的类定时从 PLC 读取标签数据
///
/// 与 S7PollingService 的区别：
///   - S7PollingService 按 DB 块读取 byte[]，使用 Struct.FromBytes() 反序列化
///   - TagPollingService 通过 IPlcClient 按标签名读取，使用 PlcTagSerializer
///   - 协议无关（可配合 Snap7PlcClient 或 S7CommPlusPlcClient 使用）
///
/// 配置方式（两种）：
///   1. appsettings.json 中定义 PlcTagPolls 数组
///   2. 代码中 AddPoll<T>() 直接注册
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

    /// <summary>从配置注册轮询任务</summary>
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
            AddPoll(type, cfg.PollIntervalMs);
        }
    }

    /// <summary>代码注册轮询任务</summary>
    public void AddPoll<T>(int pollIntervalMs = 0) where T : class, new()
        => AddPoll(typeof(T), pollIntervalMs);

    /// <summary>代码注册轮询任务</summary>
    public void AddPoll(Type type, int pollIntervalMs = 0)
    {
        var structAttr = type.GetCustomAttribute<PlcStructAttribute>();
        if (structAttr == null)
        {
            _logger?.LogWarning("[TagPoll] 类型 '{Type}' 缺少 [PlcStruct] 特性", type.Name);
            return;
        }

        var interval = pollIntervalMs > 0 ? pollIntervalMs : structAttr.RefreshRateMs;

        _registrations.Add(new TagPollRegistration
        {
            StructType = type,
            PollIntervalMs = interval
        });

        _logger?.LogInformation("[TagPoll] 注册 {Type} (间隔 {Interval}ms)", type.Name, interval);
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

                    // 输出每个标签的值
                    foreach (var prop in reg.StructType.GetProperties(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (prop.GetCustomAttribute<PlcIgnoreAttribute>() != null) continue;
                        var tagAttr = prop.GetCustomAttribute<PlcTagAttribute>();
                        if (tagAttr == null || !tagAttr.Monitored) continue;

                        var value = prop.GetValue(instance);
                        _logger?.LogDebug("[TagPoll] {Tag} = {Value}", tagAttr.Name, value);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[TagPoll] {Type}", reg.StructType.Name);
                }
            }, null, 0, reg.PollIntervalMs);

            _timers.Add(timer);
        }

        _logger?.LogInformation("[TagPoll] 启动完成，共 {Count} 个轮询任务", _registrations.Count);
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

    /// <summary>单个标签轮询注册项</summary>
    public class TagPollRegistration
    {
        public Type StructType { get; init; } = null!;
        public int PollIntervalMs { get; init; } = 1000;
    }
}
