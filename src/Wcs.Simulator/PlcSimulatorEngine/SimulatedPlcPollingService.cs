namespace Wcs.Simulator.PlcSimulatorEngine;

using Microsoft.Extensions.Logging;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem.S7;
using Wcs.Core.SignalSnapshot;
using Wcs.Core.StateCenter.Interfaces;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 模拟 PLC 轮询服务 — 替代真实的 S7PollingService
///
/// 使用 PlcSimulatorEngine 生成模拟 byte[]，
/// 通过真实管线（Struct.FromBytes → StateCenter → EventDetector）处理。
///
/// 所有 3 PLC 9 DB 块的模拟数据与真实管线完全兼容。
/// </summary>
public class SimulatedPlcPollingService
{
    private readonly PlcStructRegistry _registry;
    private readonly IStateCenter _stateCenter;
    private readonly EventDetector _eventDetector;
    private readonly SignalSnapshotCenter _snapshotCenter;
    private readonly ILogger<SimulatedPlcPollingService>? _logger;
    private readonly List<Timer> _timers = new();
    private bool _running;

    // 上次的数据缓存（用于 EventDetector 边沿检测）
    private readonly Dictionary<string, object> _previousStructs = new();

    /// <summary>每秒数据变化的概率（0~1），默认 0.3</summary>
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

    /// <summary>注册所有 18 个工位验证器</summary>
    public void RegisterAllValidators()
    {
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv01_ArrivalValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv02_TransferValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv03_MergeValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv04_BufferValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv05_WeighValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv06_SortEntryValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv07_OutboundValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv08_LiftEntryValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv09_StorageEntryValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Cv10_ExitValidator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Asrs01_Validator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Asrs02_Validator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Asrs03_Validator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Asrs04_Validator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Robot01_Validator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Robot02_Validator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Robot03_Validator());
        _eventDetector.RegisterValidator(new Wcs.Core.PlcSubsystem.Examples.Robot04_Validator());
        _logger?.LogInformation("[SimPLC] ✅ 已注册 18 个工位验证器");
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
            ("PLC2.DB3", 3, 14, PlcSimulatorEngine.GeneratePlc2_Alarms, typeof(Wcs.Core.PlcSubsystem.Examples.PLC2_DB3_StackerAlarm)),
            ("PLC3.DB1", 1, 16, PlcSimulatorEngine.GeneratePlc3_Status, typeof(Wcs.Core.PlcSubsystem.Examples.PLC3_DB1_RobotStatus)),
            ("PLC3.DB2", 2, 16, PlcSimulatorEngine.GeneratePlc3_Requests, typeof(Wcs.Core.PlcSubsystem.Examples.PLC3_DB2_RobotRequest)),
            ("PLC3.DB3", 3, 8, PlcSimulatorEngine.GeneratePlc3_Alarms, typeof(Wcs.Core.PlcSubsystem.Examples.PLC3_DB3_RobotAlarm)),
        };

        foreach (var cfg in blockConfigs)
        {
            // 从 PlcStructRegistry 获取轮询间隔
            var reg = _registry.GetAll().FirstOrDefault(r =>
                r.PlcName == cfg.Key.Split('.')[0] && r.BlockNumber == cfg.BlockNumber);

            var interval = reg?.PollIntervalMs ?? 200;

            var timer = new Timer(async _ =>
            {
                try
                {
                    // 生成模拟 byte[]（可选：概率保持不变）
                    byte[] data;
                    if (_rng.NextDouble() > ChangeProbability && _previousStructs.ContainsKey(cfg.Key))
                    {
                        data = cfg.Generate(); // 仍生成—模拟 PLC 持续运行
                    }
                    else
                    {
                        data = cfg.Generate();
                    }

                    // ===== 真实管线入口 =====
                    // Struct.FromBytes（真实反序列化）
                    var current = Wcs.Core.PlcSubsystem.SignalMapper.S7.Struct.FromBytes(
                        cfg.StructType, data, cfg.Length, 0);
                    if (current == null) return;

                    // StateCenter 无条件同步（真实）
                    SyncStateCenter(cfg.StructType, current);

                    // SignalSnapshotCenter 更新（为 EventDetector 提供 previous）
                    _snapshotCenter.Update(cfg.Key, current, cfg.StructType);

                    // EventDetector 边沿检测 + 验证器管道（真实）
                    _eventDetector.Detect(cfg.Key, current, cfg.Key.Split('.')[0], cfg.BlockNumber);

                    _previousStructs[cfg.Key] = current;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[SimPLC] {Key}", cfg.Key);
                }
            }, null, 0, interval);

            _timers.Add(timer);
            _logger?.LogInformation("[SimPLC] ▶ {Key} ({Interval}ms, {Type})",
                cfg.Key, interval, cfg.StructType.Name);
        }

        _logger?.LogInformation("[SimPLC] ✅ 3 PLC 9 DB 块模拟轮询已启动");
    }

    private static readonly Random _rng = new();

    private void SyncStateCenter(Type structType, object current)
    {
        var fields = Wcs.Core.EventDetection.FieldMetadataCache.GetFields(structType);
        foreach (var meta in fields)
        {
            var val = Wcs.Core.EventDetection.FieldMetadataCache.GetValue(meta, current);
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
