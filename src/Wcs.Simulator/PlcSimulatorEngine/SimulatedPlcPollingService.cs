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
    private readonly List<Timer> _timers = new();
    private readonly Dictionary<string, object> _previousStructs = new();
    private bool _running;
    private static readonly Random _rng = new();

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

            var timer = new Timer(async _ =>
            {
                try
                {
                    var data = cfg.Generate();
                    var current = Wcs.Core.PlcSubsystem.SignalMapper.S7.Struct.FromBytes(
                        cfg.StructType, data, cfg.Length, 0);
                    if (current == null) return;

                    SyncStateCenter(cfg.StructType, current);
                    _snapshotCenter.Update(cfg.Key, current, cfg.StructType);
                    _eventDetector.Detect(cfg.Key, current, cfg.Key.Split('.')[0], cfg.BlockNumber);
                    _previousStructs[cfg.Key] = current;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[SimPLC] {Key}", cfg.Key);
                }
            }, null, 0, interval);

            _timers.Add(timer);
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

    public void Stop()
    {
        _running = false;
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
    }
}
