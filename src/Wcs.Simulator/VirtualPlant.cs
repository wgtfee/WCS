namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;
using Wcs.Simulator.DeviceSimulator;
using Wcs.Simulator.PlcSimulator;

/// <summary>
/// Virtual Plant — 虚拟工厂门面
///
/// 将 PLC 模拟器、设备模拟器、运输生成器、混沌猴子全部组装在一起。
/// 只需一行 DI 注册即可替代真实 PLC/设备，让 WCS Core 在无硬件环境下运行。
///
/// 使用示例：
///   services.AddSingleton<VirtualPlant>();
///   var plant = sp.GetRequiredService<VirtualPlant>();
///   await plant.StartAsync();
///   await plant.RunScenarioAsync(ScenarioTemplate.StressTest());
/// </summary>
public class VirtualPlant : IDisposable
{
    /// <summary>信号源（模拟 PLC）</summary>
    public SimulatorSignalSource SignalSource { get; }

    /// <summary>输送线模拟器集合</summary>
    public List<ConveyorSimulator> Conveyors { get; } = new();

    /// <summary>提升机模拟器集合</summary>
    public List<LiftSimulator> Lifts { get; } = new();

    /// <summary>堆垛机模拟器集合</summary>
    public List<AsrsSimulator> AsrsMachines { get; } = new();

    /// <summary>机器人模拟器集合</summary>
    public List<RobotSimulator> Robots { get; } = new();

    /// <summary>运输生成器</summary>
    public TransportGenerator Generator { get; }

    /// <summary>混沌猴子</summary>
    public ChaosMonkey Chaos { get; }

    /// <summary>信号回放播放器</summary>
    public SignalReplayPlayer ReplayPlayer { get; }

    /// <summary>场景运行器</summary>
    public ScenarioRunner Runner { get; }

    private readonly ILogger<VirtualPlant>? _logger;
    private readonly List<IDisposable> _disposables = new();

    /// <summary>
    /// 已注册的设备数
    /// </summary>
    public int DeviceCount => Conveyors.Count + Lifts.Count + AsrsMachines.Count + Robots.Count;

    public VirtualPlant(
        TransportGenerator generator,
        ILogger<VirtualPlant>? logger = null)
    {
        SignalSource = new SimulatorSignalSource("VirtualPLC");
        Generator = generator;
        Chaos = new ChaosMonkey(SignalSource);
        ReplayPlayer = new SignalReplayPlayer(SignalSource);
        Runner = new ScenarioRunner(generator, Chaos, ReplayPlayer);

        _logger = logger;
    }

    /// <summary>
    /// 添加输送线
    /// </summary>
    public ConveyorSimulator AddConveyor(string deviceId, string name = "", int transportMs = 3000)
    {
        var sim = new ConveyorSimulator(deviceId,
            string.IsNullOrEmpty(name) ? $"Conveyor {deviceId}" : name,
            SignalSource) { TransportTimeMs = transportMs };
        Conveyors.Add(sim);
        Chaos.RegisterDevice(sim);
        return sim;
    }

    /// <summary>
    /// 添加提升机
    /// </summary>
    public LiftSimulator AddLift(string deviceId, string name = "", int transportMs = 5000)
    {
        var sim = new LiftSimulator(deviceId,
            string.IsNullOrEmpty(name) ? $"Lift {deviceId}" : name,
            SignalSource) { TransportTimeMs = transportMs };
        Lifts.Add(sim);
        Chaos.RegisterDevice(sim);
        return sim;
    }

    /// <summary>
    /// 添加堆垛机
    /// </summary>
    public AsrsSimulator AddAsrs(string deviceId, string name = "", int transportMs = 8000)
    {
        var sim = new AsrsSimulator(deviceId,
            string.IsNullOrEmpty(name) ? $"ASRS {deviceId}" : name,
            SignalSource) { TransportTimeMs = transportMs };
        AsrsMachines.Add(sim);
        Chaos.RegisterDevice(sim);
        return sim;
    }

    /// <summary>
    /// 添加机器人
    /// </summary>
    public RobotSimulator AddRobot(string deviceId, string name = "", int transportMs = 2000)
    {
        var sim = new RobotSimulator(deviceId,
            string.IsNullOrEmpty(name) ? $"Robot {deviceId}" : name,
            SignalSource) { TransportTimeMs = transportMs };
        Robots.Add(sim);
        Chaos.RegisterDevice(sim);
        return sim;
    }

    /// <summary>
    /// 运行测试场景
    /// </summary>
    public Task<ScenarioResult> RunScenarioAsync(ScenarioTemplate scenario, CancellationToken ct = default)
        => Runner.RunAsync(scenario, ct);

    /// <summary>
    /// 直接运行快速测试（1 tps, 2 分钟）
    /// </summary>
    public Task<ScenarioResult> QuickTestAsync(CancellationToken ct = default)
        => RunScenarioAsync(ScenarioTemplate.QuickTest(), ct);

    /// <summary>
    /// 直接运行压力测试（10 tps, 30 分钟）
    /// </summary>
    public Task<ScenarioResult> StressTestAsync(CancellationToken ct = default)
        => RunScenarioAsync(ScenarioTemplate.StressTest(), ct);

    /// <summary>
    /// 直接运行韧性测试（3 tps, 60 分钟, 10% 故障概率）
    /// </summary>
    public Task<ScenarioResult> ResilienceTestAsync(CancellationToken ct = default)
        => RunScenarioAsync(ScenarioTemplate.ResilienceTest(), ct);

    /// <summary>
    /// 快速构建一个默认的虚拟工厂拓扑
    /// </summary>
    public void BuildDefaultTopology()
    {
        AddConveyor("CV01", "Inbound Conveyor");
        AddConveyor("CV02", "Transfer Conveyor");
        AddConveyor("CV03", "Outbound Conveyor");
        AddLift("LIFT01", "Main Lift");
        AddAsrs("ASRS01", "ASRS Aisle 1");
        AddAsrs("ASRS02", "ASRS Aisle 2");
        AddRobot("ROBOT01", "Unload Robot");

        _logger?.LogInformation("Default topology built: {Count} devices", DeviceCount);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _disposables.Clear();
    }
}
