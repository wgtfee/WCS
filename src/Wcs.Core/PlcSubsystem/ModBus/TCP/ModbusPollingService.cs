using System.Reflection;
using Microsoft.Extensions.Logging;
using Wcs.Core.EventDetection;
using Wcs.Core.SignalSnapshot;
using Wcs.Core.PlcSubsystem.Label;

namespace Wcs.Core.PlcSubsystem.Modbus;

/// <summary>
/// Modbus 标签轮询服务 — 对标 S7PollingService / TagPollingService
///
/// V10.2 改进：PeriodicTimer 异步循环防 tick 重叠；实例预创建复用，消除每 tick 反射激活。
/// </summary>
public class ModbusPollingService : IDisposable
{
    private readonly ModbusTagSerializer _serializer;
    private readonly ILogger<ModbusPollingService>? _logger;
    private readonly SignalSnapshotCenter? _snapshotCenter;
    private readonly EventDetector? _eventDetector;
    private readonly List<ModbusPollRegistration> _registrations = new();
    private readonly List<PollLoop> _loops = new();
    private volatile bool _running;
    private bool _disposed;

    public ModbusPollingService(
        ModbusTagSerializer serializer,
        ILogger<ModbusPollingService>? logger = null,
        SignalSnapshotCenter? snapshotCenter = null,
        EventDetector? eventDetector = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger;
        _snapshotCenter = snapshotCenter;
        _eventDetector = eventDetector;
    }

    public void AddFromConfig(IEnumerable<TagPollConfig> configs)
    {
        foreach (var cfg in configs)
        {
            var type = Type.GetType(cfg.StructType);
            if (type == null)
            {
                _logger?.LogWarning("[ModbusPoll] 找不到类型 '{Type}'", cfg.StructType);
                continue;
            }
            AddPoll(type);
        }
    }

    public void AddPoll<T>() where T : class, new() => AddPoll(typeof(T));

    public void AddPoll(Type type)
    {
        var blockAttr = type.GetCustomAttribute<PlcModbusBlockAttribute>();
        if (blockAttr == null)
        {
            _logger?.LogWarning("[ModbusPoll] 类型 '{Type}' 缺少 [PlcModbusBlock] 特性", type.Name);
            return;
        }

        _registrations.Add(new ModbusPollRegistration
        {
            StructType = type,
            PollIntervalMs = blockAttr.RefreshRateMs,
            BlockKey = type.FullName ?? type.Name
        });

        _logger?.LogInformation("[ModbusPoll] 注册 {Type} (间隔 {Interval}ms)", type.Name, blockAttr.RefreshRateMs);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;
        _running = true;

        foreach (var reg in _registrations)
        {
            var instance = Activator.CreateInstance(reg.StructType);
            if (instance == null)
            {
                _logger?.LogError("[ModbusPoll] 无法实例化 {Type}", reg.StructType.Name);
                continue;
            }

            var cts = new CancellationTokenSource();
            _loops.Add(new PollLoop(cts, RunPollLoopAsync(reg, instance, cts.Token)));
        }

        _logger?.LogInformation("[ModbusPoll] 启动完成，共 {Count} 个类型", _loops.Count);
    }

    private async Task RunPollLoopAsync(ModbusPollRegistration reg, object instance, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, reg.PollIntervalMs)));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _serializer.ReadAsync(instance).ConfigureAwait(false);

                    // 事件链路
                    _snapshotCenter?.Update(reg.BlockKey!, instance, reg.StructType);
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
                    _logger?.LogError(ex, "[ModbusPoll] {Type}", reg.StructType.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

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

    public class ModbusPollRegistration
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
