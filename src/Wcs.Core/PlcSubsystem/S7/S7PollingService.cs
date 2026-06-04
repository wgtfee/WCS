namespace Wcs.Core.PlcSubsystem.S7;

using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem.SignalMapper.S7;
using Wcs.Core.SignalSnapshot;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// PLC 轮询服务 — 读 PLC → StateCenter(无条件) → EventDetector(边沿→业务事件)
///
/// V10.1 改进：
///   1. SignalSnapshotCenter 统一管理 Current/Previous
///   2. StateCenter 永远同步 PLC（无条件，不经过验证器）
///   3. EventDetector 走 FieldMetadataCache（零反射）
/// </summary>
public class S7PollingService
{
    private readonly PlcStructRegistry _registry;
    private readonly IStateCenter _stateCenter;
    private readonly EventDetector _eventDetector;
    private readonly SignalSnapshotCenter _snapshotCenter;
    private readonly ILogger<S7PollingService>? _logger;
    private readonly List<Timer> _timers = new();
    private bool _running;

    public S7PollingService(
        PlcStructRegistry registry,
        IStateCenter stateCenter,
        EventDetector eventDetector,
        SignalSnapshotCenter snapshotCenter,
        ILogger<S7PollingService>? logger = null)
    {
        _registry = registry;
        _stateCenter = stateCenter;
        _eventDetector = eventDetector;
        _snapshotCenter = snapshotCenter;
        _logger = logger;
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

                    var blockKey = $"{reg.PlcName}.DB{reg.BlockNumber}";

                    // 1. StateCenter 无条件同步
                    SyncStateCenter(reg.StructType, current);

                    // 2. 快照更新（为 EventDetector 提供 previous）
                    _snapshotCenter.Update(blockKey, current, reg.StructType);

                    // 3. EventDetector 边沿检测 → 业务事件
                    _eventDetector.Detect(blockKey, current, reg.PlcName, reg.BlockNumber);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[S7] {Plc} DB{Block}", reg.PlcName, reg.BlockNumber);
                }
            }, null, 0, reg.PollIntervalMs);

            _timers.Add(timer);
        }
    }

    private void SyncStateCenter(Type structType, object current)
    {
        var fields = FieldMetadataCache.GetFields(structType);
        foreach (var meta in fields)
        {
            var newVal = FieldMetadataCache.GetValue(meta, current);
            if (meta.DeviceId == null) continue;

            var status = newVal is bool b && b ? DeviceStatusEnum.Running : DeviceStatusEnum.Idle;
            _stateCenter.UpdateDeviceState(meta.DeviceId, new DeviceState
            {
                DeviceId = meta.DeviceId,
                Status = status,
                LastUpdateTime = DateTime.UtcNow
            });
        }
    }

    public void Stop()
    {
        _running = false;
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }
}
