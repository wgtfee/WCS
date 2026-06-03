namespace Wcs.Simulator;

using Microsoft.Extensions.Logging;

/// <summary>
/// 场景模板 — 预定义测试场景
/// </summary>
public class ScenarioTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10);
    public int TransportTps { get; set; } = 2;
    public double FaultProbability { get; set; }
    public bool EnableReplay { get; set; }
    public string? ReplayFilePath { get; set; }

    public static ScenarioTemplate QuickTest() => new()
    {
        Name = "Quick Test",
        Description = "1 tps, 2 min, no faults",
        Duration = TimeSpan.FromMinutes(2),
        TransportTps = 1
    };

    public static ScenarioTemplate StressTest() => new()
    {
        Name = "Stress Test",
        Description = "10 tps, 30 min, no faults",
        Duration = TimeSpan.FromMinutes(30),
        TransportTps = 10
    };

    public static ScenarioTemplate ResilienceTest() => new()
    {
        Name = "Resilience Test",
        Description = "3 tps, 60 min, 10% fault probability",
        Duration = TimeSpan.FromMinutes(60),
        TransportTps = 3,
        FaultProbability = 0.10
    };

    public static ScenarioTemplate ReplayTest(string filePath) => new()
    {
        Name = "Replay Test",
        Description = $"Replay signals from {filePath}",
        Duration = TimeSpan.FromMinutes(10),
        EnableReplay = true,
        ReplayFilePath = filePath
    };

    public static ScenarioTemplate LongRunTest() => new()
    {
        Name = "72h Long Run",
        Description = "5 tps, 72 hours, 5% fault",
        Duration = TimeSpan.FromHours(72),
        TransportTps = 5,
        FaultProbability = 0.05
    };
}

/// <summary>
/// 场景运行器 — 运行预定义测试场景
/// </summary>
public class ScenarioRunner
{
    private readonly TransportGenerator _generator;
    private readonly ChaosMonkey? _chaosMonkey;
    private readonly SignalReplayPlayer? _replayPlayer;
    private readonly ILogger<ScenarioRunner>? _logger;

    public ScenarioRunner(TransportGenerator generator,
        ChaosMonkey? chaosMonkey = null,
        SignalReplayPlayer? replayPlayer = null,
        ILogger<ScenarioRunner>? logger = null)
    {
        _generator = generator;
        _chaosMonkey = chaosMonkey;
        _replayPlayer = replayPlayer;
        _logger = logger;
    }

    public async Task<ScenarioResult> RunAsync(ScenarioTemplate scenario, CancellationToken ct = default)
    {
        _logger?.LogInformation("=== Scenario START: {Name} ({Desc}) ===",
            scenario.Name, scenario.Description);

        var startTime = DateTime.UtcNow;
        var result = new ScenarioResult { ScenarioName = scenario.Name };

        try
        {
            // 配置参数
            _generator.TasksPerSecond = scenario.TransportTps;
            if (_chaosMonkey != null)
                _chaosMonkey.FaultProbability = scenario.FaultProbability;

            // 并行启动生成器和混沌猴子
            var tasks = new List<Task>
            {
                _generator.StartAsync(ct)
            };

            if (_chaosMonkey != null && scenario.FaultProbability > 0)
                tasks.Add(_chaosMonkey.StartAsync(ct));

            if (scenario.EnableReplay && _replayPlayer != null && scenario.ReplayFilePath != null)
                tasks.Add(_replayPlayer.PlayFileAsync(scenario.ReplayFilePath, ct).ContinueWith(_ => { }, ct));

            // 运行指定时长
            await Task.WhenAny(
                Task.WhenAll(tasks),
                Task.Delay(scenario.Duration, ct)
            );

            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Success = true;
            result.Message = "Cancelled";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = ex.Message;
            _logger?.LogError(ex, "Scenario {Name} failed", scenario.Name);
        }
        finally
        {
            _generator.Stop();
            _chaosMonkey?.Stop();
            result.Duration = DateTime.UtcNow - startTime;
            result.TasksGenerated = _generator.Generated;
            _logger?.LogInformation("=== Scenario END: {Name} ({Duration}) ===",
                scenario.Name, result.Duration);
        }

        return result;
    }
}

public class ScenarioResult
{
    public string ScenarioName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public TimeSpan Duration { get; set; }
    public long TasksGenerated { get; set; }
}
