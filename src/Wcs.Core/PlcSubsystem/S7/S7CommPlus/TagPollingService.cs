using System.Reflection;
using Microsoft.Extensions.Logging;
using Wcs.Core.EventDetection;
using Wcs.Core.SignalSnapshot;

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
///
/// 事件链路（与 S7PollingService 一致）：
///   读取后 → 快照更新 → EventDetector 边沿检测
///
/// V10.2 改进：
///   1. 每个注册类型由独立的 PeriodicTimer 异步循环驱动，单次读取超过间隔时跳过重叠 tick；
///   2. 实例在循环外预创建并复用（循环内串行 await，无并发访问），消除每 tick 反射激活。
/// </summary>
public class TagPollingService : IDisposable
{
    private readonly PlcTagSerializer _serializer;
    private readonly ILogger<TagPollingService>? _logger;
    private readonly SignalSnapshotCenter? _snapshotCenter;
    private readonly EventDetector? _eventDetector;
    private readonly List<TagPollRegistration> _registrations = new();
    private readonly List<PollLoop> _loops = new();
    private volatile bool _running;
    private bool _disposed;

    public TagPollingService(
        PlcTagSerializer serializer,
        ILogger<TagPollingService>? logger = null,
        SignalSnapshotCenter? snapshotCenter = null,
        EventDetector? eventDetector = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger;
        _snapshotCenter = snapshotCenter;
        _eventDetector = eventDetector;
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
            PollIntervalMs = structAttr.RefreshRateMs,
            BlockKey = type.FullName ?? type.Name
        });

        _logger?.LogInformation("[TagPoll] 注册 {Type} (间隔 {Interval}ms)", type.Name, structAttr.RefreshRateMs);
    }

    /// <summary>启动所有轮询任务</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;
        _running = true;

        foreach (var reg in _registrations)
        {
            // 每个 poll 循环独占一个实例：循环体内严格串行 await，复用安全。
            var instance = Activator.CreateInstance(reg.StructType);
            if (instance == null)
            {
                _logger?.LogError("[TagPoll] 无法实例化 {Type}", reg.StructType.Name);
                continue;
            }

            var cts = new CancellationTokenSource();
            _loops.Add(new PollLoop(cts, RunPollLoopAsync(reg, instance, cts.Token)));
        }

        _logger?.LogInformation("[TagPoll] 启动完成，共 {Count} 个类型", _loops.Count);
    }

    private async Task RunPollLoopAsync(TagPollRegistration reg, object instance, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, reg.PollIntervalMs)));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _serializer.ReadAsync(instance).ConfigureAwait(false);

                    // === 事件链路（与 S7PollingService 一致） ===
                    // 1. 快照更新（为 EventDetector 提供 previous）
                    _snapshotCenter?.Update(reg.BlockKey!, instance, reg.StructType);

                    // 2. EventDetector 边沿检测 → 业务事件
                    if (_eventDetector != null)
                        await _eventDetector.DetectAsync(reg.BlockKey!, instance, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[TagPoll] {Type}", reg.StructType.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    /// <summary>停止所有轮询任务</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        foreach (var loop in _loops)
        {
            loop.Cancel();
        }
        _loops.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    public class TagPollRegistration
    {
        public Type StructType { get; init; } = null!;
        public int PollIntervalMs { get; init; } = 1000;
        /// <summary>预计算的块键（避免每 tick 字符串插值）</summary>
        public string? BlockKey { get; init; }
    }

    private sealed record PollLoop(CancellationTokenSource Cancellation, Task Task)
    {
        public void Cancel()
        {
            try { Cancellation.Cancel(); } catch { /* 已取消 */ }
            Cancellation.Dispose();
        }
    }
}
