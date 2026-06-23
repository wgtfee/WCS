using System.Reflection;
using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem.Label;

namespace Wcs.Core.PlcSubsystem.OpcUa;

/// <summary>
/// OPC UA 标签轮询服务 — 对标 S7PollingService / TagPollingService
/// </summary>
public class OpcUaPollingService : IDisposable
{
    private readonly OpcUaTagSerializer _serializer;
    private readonly ILogger<OpcUaPollingService>? _logger;
    private readonly List<OpcUaPollRegistration> _registrations = new();
    private readonly List<Timer> _timers = new();
    private bool _running;

    public OpcUaPollingService(
        OpcUaTagSerializer serializer,
        ILogger<OpcUaPollingService>? logger = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger;
    }

    public void AddFromConfig(IEnumerable<TagPollConfig> configs)
    {
        foreach (var cfg in configs)
        {
            var type = Type.GetType(cfg.StructType);
            if (type == null)
            {
                _logger?.LogWarning("[OpcUaPoll] 找不到类型 '{Type}'", cfg.StructType);
                continue;
            }
            AddPoll(type);
        }
    }

    public void AddPoll<T>() where T : class, new() => AddPoll(typeof(T));

    public void AddPoll(Type type)
    {
        var blockAttr = type.GetCustomAttribute<PlcOpcUaBlockAttribute>();
        if (blockAttr == null)
        {
            _logger?.LogWarning("[OpcUaPoll] 类型 '{Type}' 缺少 [PlcOpcUaBlock] 特性", type.Name);
            return;
        }

        _registrations.Add(new OpcUaPollRegistration
        {
            StructType = type,
            PollIntervalMs = blockAttr.RefreshRateMs
        });

        _logger?.LogInformation("[OpcUaPoll] 注册 {Type} (间隔 {Interval}ms)", type.Name, blockAttr.RefreshRateMs);
    }

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
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[OpcUaPoll] {Type}", reg.StructType.Name);
                }
            }, null, 0, reg.PollIntervalMs);

            _timers.Add(timer);
        }

        _logger?.LogInformation("[OpcUaPoll] 启动完成，共 {Count} 个类型", _registrations.Count);
    }

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

    public class OpcUaPollRegistration
    {
        public Type StructType { get; init; } = null!;
        public int PollIntervalMs { get; init; } = 1000;
    }
}
