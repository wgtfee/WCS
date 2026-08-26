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
///
/// V10.2 改进：
///   1. 每个注册块由独立的 PeriodicTimer 异步循环驱动（替代 Timer(async _ => ...)）：
///      - 单次读取耗时超过轮询间隔时自动跳过重叠 tick，避免同块读取堆积；
///      - 消除 async void 语义，异常始终可观测。
///   2. blockKey 等不变字符串在循环外预计算。
/// </summary>
public class S7PollingService
{
    private readonly PlcStructRegistry _registry;
    private readonly IStateCenter _stateCenter;
    private readonly EventDetector _eventDetector;
    private readonly SignalSnapshotCenter _snapshotCenter;
    private readonly ILogger<S7PollingService>? _logger;
    private readonly List<PollLoop> _loops = new();
    private volatile bool _running;

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
            var cts = new CancellationTokenSource();
            var loop = new PollLoop(reg, RunPollLoopAsync(reg, cts.Token), cts);
            _loops.Add(loop);
        }
    }

    private async Task RunPollLoopAsync(PlcBlockRegistration reg, CancellationToken cancellationToken)
    {
        // 不变字符串只算一次
        var blockKey = $"{reg.PlcName}.DB{reg.BlockNumber}";

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1, reg.PollIntervalMs)));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await PollOnceAsync(reg, blockKey, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[S7] {Plc} DB{Block}", reg.PlcName, reg.BlockNumber);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private async Task PollOnceAsync(PlcBlockRegistration reg, string blockKey, CancellationToken cancellationToken)
    {
        var conn = _registry.ReadPool.Get(reg.PlcName);
        if (conn == null) return;

        var (data, result, error) = await conn.ReadAsync(
            reg.BlockNumber, reg.StartByte, reg.Length).ConfigureAwait(false);
        if (result != 0 || data == null || data.Length == 0) return;

        var current = Struct.FromBytes(reg.StructType, data, reg.Length, 0);
        if (current == null) return;

        // 1. StateCenter 无条件同步
        SyncStateCenter(reg.StructType, current);

        // 2. 快照更新（为 EventDetector 提供 previous）
        _snapshotCenter.Update(blockKey, current, reg.StructType);

        // 3. EventDetector 边沿检测 → 业务事件
        await _eventDetector.DetectAsync(blockKey, current, reg.PlcName, reg.BlockNumber, cancellationToken)
            .ConfigureAwait(false);
    }

    private void SyncStateCenter(Type structType, object current)
    {
        var fields = FieldMetadataCache.GetFields(structType);
        foreach (var meta in fields)
        {
            if (meta.DeviceId == null) continue;

            var newVal = FieldMetadataCache.GetValue(meta, current);
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
        if (!_running) return;
        _running = false;

        foreach (var loop in _loops)
        {
            loop.Cancel();
        }
        _loops.Clear();
    }

    private sealed record PollLoop(PlcBlockRegistration Registration, Task Task, CancellationTokenSource Cancellation)
    {
        public void Cancel()
        {
            try { Cancellation.Cancel(); } catch { /* 已取消 */ }
            Cancellation.Dispose();
        }
    }
}
