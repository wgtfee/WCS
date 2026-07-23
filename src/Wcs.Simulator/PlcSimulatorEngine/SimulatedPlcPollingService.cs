namespace Wcs.Simulator.PlcSimulatorEngine;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem.S7;
using Wcs.Core.PlcSubsystem.Validation.Examples;
using Wcs.Core.SignalSnapshot;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

public class SimulatedPlcPollingService
{
    private readonly PlcStructRegistry _registry;
    private readonly IStateCenter _stateCenter;
    private readonly EventDetector _eventDetector;
    private readonly SignalSnapshotCenter _snapshotCenter;
    private readonly ILogger<SimulatedPlcPollingService>? _logger;
    private readonly List<Task> _pollingTasks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _previousStructs = new();
    private CancellationTokenSource? _pollingCts;
    private bool _running;

    public double ChangeProbability { get; set; } = 0.3;

    public SimulatedPlcPollingService(
        PlcStructRegistry registry,
        IStateCenter stateCenter,
        EventDetector eventDetector,
        SignalSnapshotCenter snapshotCenter,
        ILogger<SimulatedPlcPollingService>? logger = null)
    {
        _registry = registry;
        _stateCenter = stateCenter;
        _eventDetector = eventDetector;
        _snapshotCenter = snapshotCenter;
        _logger = logger;
    }

    /// <summary>注册默认验证器 — AlwaysPass，所有信号放行，方便观察完整链路</summary>
    public void RegisterDefaultValidators()
    {
        _eventDetector.RegisterValidator(new AlwaysPassValidator());
        _logger?.LogInformation("[SimPLC] ✅ 已注册 AlwaysPassValidator — 所有信号默认通过");
    }

    /// <summary>启动所有模拟 PLC 轮询</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        var pollingCts = new CancellationTokenSource();
        _pollingCts = pollingCts;

        var blockConfigs = new (string Key, int BlockNumber, int Length, Func<byte[]> Generate, Type StructType)[]
        {
            ("PLC1.DB1", 1, 40, PlcSimulatorEngine.GeneratePlc1_Status, typeof(Wcs.Core.PlcSubsystem.Examples.PLC1_DB1_ConveyorStatus)),
            ("PLC1.DB2", 2, 20, PlcSimulatorEngine.GeneratePlc1_Requests, typeof(Wcs.Core.PlcSubsystem.Examples.PLC1_DB2_ConveyorRequest)),
            ("PLC1.DB3", 3, 20, PlcSimulatorEngine.GeneratePlc1_Alarms, typeof(Wcs.Core.PlcSubsystem.Examples.PLC1_DB3_ConveyorAlarm)),
            ("PLC2.DB1", 1, 24, PlcSimulatorEngine.GeneratePlc2_Status, typeof(Wcs.Core.PlcSubsystem.Examples.PLC2_DB1_StackerStatus)),
            ("PLC2.DB2", 2, 24, PlcSimulatorEngine.GeneratePlc2_Requests, typeof(Wcs.Core.PlcSubsystem.Examples.PLC2_DB2_StackerRequest)),
            ("PLC2.DB3", 3, 16, PlcSimulatorEngine.GeneratePlc2_Alarms, typeof(Wcs.Core.PlcSubsystem.Examples.PLC2_DB3_StackerAlarm)),
            ("PLC3.DB1", 1, 16, PlcSimulatorEngine.GeneratePlc3_Status, typeof(Wcs.Core.PlcSubsystem.Examples.PLC3_DB1_RobotStatus)),
            ("PLC3.DB2", 2, 16, PlcSimulatorEngine.GeneratePlc3_Requests, typeof(Wcs.Core.PlcSubsystem.Examples.PLC3_DB2_RobotRequest)),
            ("PLC3.DB3", 3, 8, PlcSimulatorEngine.GeneratePlc3_Alarms, typeof(Wcs.Core.PlcSubsystem.Examples.PLC3_DB3_RobotAlarm)),
        };

        foreach (var cfg in blockConfigs)
        {
            var reg = _registry.GetAll().FirstOrDefault(r =>
                r.PlcName == cfg.Key.Split('.')[0] && r.BlockNumber == cfg.BlockNumber);
            var interval = reg?.PollIntervalMs ?? 200;

            _pollingTasks.Add(Task.Run(
                () => PollBlockAsync(cfg, interval, pollingCts.Token),
                pollingCts.Token));
        }

        _logger?.LogInformation("[SimPLC] ✅ 3 PLC 9 DB 块模拟轮询已启动");
    }

    private void SyncStateCenter(Type structType, object current)
    {
        var fields = FieldMetadataCache.GetFields(structType);
        foreach (var meta in fields)
        {
            var val = FieldMetadataCache.GetValue(meta, current);
            if (meta.DeviceId == null) continue;
            var status = val is bool b && b ? DeviceStatusEnum.Running : DeviceStatusEnum.Idle;
            _stateCenter.UpdateDeviceState(meta.DeviceId, new DeviceState
            {
                DeviceId = meta.DeviceId,
                Status = status,
                LastUpdateTime = DateTime.UtcNow
            });
        }
    }

    private async Task PollBlockAsync(
        (string Key, int BlockNumber, int Length, Func<byte[]> Generate, Type StructType) cfg,
        int intervalMs,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        try
        {
            do
            {
                var data = cfg.Generate();
                var current = Wcs.Core.PlcSubsystem.SignalMapper.S7.Struct.FromBytes(
                    cfg.StructType, data, cfg.Length, 0);
                if (current is not null)
                {
                    SyncStateCenter(cfg.StructType, current);
                    _snapshotCenter.Update(cfg.Key, current, cfg.StructType);
                    await _eventDetector.DetectAsync(
                        cfg.Key,
                        current,
                        cfg.Key.Split('.')[0],
                        cfg.BlockNumber,
                        cancellationToken);
                    _previousStructs[cfg.Key] = current;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal simulator shutdown.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SimPLC] {Key}", cfg.Key);
        }
    }

    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;

        var cts = _pollingCts;
        _pollingCts = null;
        if (cts is null) return;

        cts.Cancel();
        try
        {
            await Task.WhenAll(_pollingTasks);
        }
        catch (OperationCanceledException)
        {
            // Normal simulator shutdown.
        }
        finally
        {
            _pollingTasks.Clear();
            cts.Dispose();
        }
    }
}
