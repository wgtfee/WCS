namespace Wcs.Core.PlcSubsystem.S7;

using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem.SignalMapper.S7;
using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// PLC 轮询服务 — 读 PLC → byte[] → struct → StateCenter(永远同步) → EventDetector → EventBus
///
/// 2024-06 修正：
///   1. StateCenter 先更新，再走验证 —— 保证监控能看到真实 PLC 状态
///   2. 验证器只拦截业务事件，不阻断 StateCenter 更新
///   3. EventDetector 负责边沿检测 + 领域事件生成
/// </summary>
public class S7PollingService
{
    private readonly PlcStructRegistry _registry;
    private readonly IStateCenter _stateCenter;
    private readonly IEventBus _eventBus;
    private readonly EventDetector _eventDetector;
    private readonly List<ISignalValidator> _signalValidators = new();
    private readonly ILogger<S7PollingService>? _logger;
    private readonly List<Timer> _timers = new();
    private bool _running;

    public S7PollingService(
        PlcStructRegistry registry,
        IStateCenter stateCenter,
        IEventBus eventBus,
        EventDetector eventDetector,
        ILogger<S7PollingService>? logger = null)
    {
        _registry = registry;
        _stateCenter = stateCenter;
        _eventBus = eventBus;
        _eventDetector = eventDetector;
        _logger = logger;
    }

    /// <summary>注册信号验证器（验证通过后才发布业务事件）</summary>
    public void RegisterValidator(ISignalValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _signalValidators.Add(validator);
        _eventDetector.RegisterValidator(validator);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        foreach (var reg in _registry.GetAll())
        {
            var timer = new Timer(async _ =>
            {
                try
                {
                    var conn = _registry.ReadPool.Get(reg.PlcName);
                    if (conn == null) return;

                    var (data, result, error) = await conn.ReadAsync(
                        reg.BlockNumber, reg.StartByte, reg.Length);
                    if (result != 0 || data == null || data.Length == 0) return;

                    var current = Struct.FromBytes(reg.StructType, data, reg.Length, 0);
                    if (current == null) return;

                    // ===== 第一步：StateCenter 永远同步 PLC =====
                    // 先更新 StateCenter，不经过验证器
                    // 保证监控/UI/报警系统能看到真实 PLC 状态
                    SyncStateCenter(reg.StructType, current, reg.PreviousStruct);

                    // ===== 第二步：EventDetector 边沿检测 → 业务事件 =====
                    // 只在字段边沿变化时生成事件
                    // 经过验证管道，被拒的事件不发布
                    _eventDetector.DetectAndPublish(reg.StructType, current, reg.PreviousStruct);

                    reg.PreviousStruct = current;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[S7] {Plc} DB{Block}", reg.PlcName, reg.BlockNumber);
                }
            }, null, 0, reg.PollIntervalMs);

            _timers.Add(timer);
        }
    }

    /// <summary>
    /// 同步 StateCenter — 无条件，不经过验证器
    /// 让监控系统始终看到真实 PLC 状态
    /// </summary>
    private void SyncStateCenter(Type structType, object current, object? previous)
    {
        var fields = structType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        foreach (var field in fields)
        {
            var newVal = field.GetValue(current);
            var oldVal = previous?.GetType() == structType ? field.GetValue(previous) : null;
            if (Equals(newVal, oldVal)) continue;

            var deviceId = ExtractDeviceId(field.Name);
            if (deviceId == null) continue;

            var status = newVal is bool b && b ? DeviceStatusEnum.Running : DeviceStatusEnum.Idle;
            _stateCenter.UpdateDeviceState(deviceId, new DeviceState
            {
                DeviceId = deviceId,
                Status = status,
                LastUpdateTime = DateTime.UtcNow
            });
        }
    }

    private static string? ExtractDeviceId(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return null;
        var parts = fieldName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : null;
    }

    public void Stop()
    {
        _running = false;
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }
}
