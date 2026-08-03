namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Simulator.Governance;
using Wcs.Simulator.HilVerification;

/// <summary>
/// S10 read-only catalog for the complete Simulation & Verification line.
/// It exposes capability and safety metadata only; it cannot execute scenarios,
/// inject faults, drive PLC/RGV equipment, or start/accept a real HIL session.
/// </summary>
[ApiController]
[Route("api/simulation/verification")]
public sealed class SimulationVerificationOverviewController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public SimulationVerificationOverviewController(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("overview")]
    public IActionResult GetOverview()
    {
        if (string.Equals(_environment.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var simulationAllowed = GetSimulationDecision().Allowed;
        var hilAllowed = GetHilDecision().Allowed;
        if (!simulationAllowed && !hilAllowed)
            return NotFound();

        return Ok(new SimulationVerificationOverview
        {
            Stage = "S10",
            Environment = _environment.EnvironmentName,
            ReadOnly = true,
            RemoteControlAllowed = false,
            SimulationInspectionAvailable = simulationAllowed,
            HilInspectionAvailable = hilAllowed,
            RealHilExecuted = false,
            ProtocolValidated = false,
            MechanicalSafetyAccepted = false,
            SiteAccepted = false,
            RealHilEvidenceRequiredForCompletion = true,
            Stages = BuildStages(simulationAllowed, hilAllowed)
        });
    }

    private SimulationAccessDecision GetSimulationDecision()
    {
        var options = _configuration
            .GetSection(SimulationGovernanceOptions.SectionName)
            .Get<SimulationGovernanceOptions>() ?? new SimulationGovernanceOptions();
        return SimulationBoundaryGuard.Evaluate(
            _environment.EnvironmentName,
            options,
            _configuration.GetSection("Simulator").GetValue<bool>("Enabled"));
    }

    private HilEnvironmentAccessDecision GetHilDecision()
    {
        // ConfigurationBinder appends arrays. Bind into an empty allow-list so duplicate
        // initialized values cannot accidentally make a valid HIL profile fail validation.
        var options = new HilVerificationOptions { AllowedEnvironments = [] };
        _configuration.GetSection(HilVerificationOptions.SectionName).Bind(options);
        return HilEnvironmentBoundaryGuard.Evaluate(_environment.EnvironmentName, options);
    }

    private static IReadOnlyList<SimulationVerificationStage> BuildStages(
        bool simulationAllowed,
        bool hilAllowed) =>
    [
        Stage("S0", "治理与隔离", "场景版本、Seed、Evidence 与环境边界", "/api/simulation/governance", simulationAllowed, false, false),
        Stage("S1", "场景引擎", "DSL、虚拟时钟、Checkpoint 与 Replay", "/api/simulation/scenarios", simulationAllowed, true, false),
        Stage("S2", "虚拟 PLC", "DB 块、信号变化与故障注入结果检查", "/api/simulation/virtual-plc", simulationAllowed, true, false),
        Stage("S3", "虚拟 RGV", "区段运动、位置与确定性检查", "/api/simulation/virtual-rgv", simulationAllowed, true, false),
        Stage("S4", "虚拟交通", "预约、冲突、等待图与死锁检查", "/api/simulation/virtual-traffic", simulationAllowed, true, false),
        Stage("S5", "外部依赖故障", "Retry、Circuit、恢复与幂等检查", "/api/simulation/virtual-external", simulationAllowed, true, false),
        Stage("S6", "合成健康与 RUL", "健康、Forecast Oracle 与 Outcome 检查", "/api/simulation/virtual-health", simulationAllowed, true, false),
        Stage("S7", "全链路集成恢复", "Mission、Checkpoint、Replay 与 exactly-once 检查", "/api/simulation/virtual-integration", simulationAllowed, true, false),
        Stage("S8", "容量长稳与 HIL 准备", "容量 Profile、虚拟长稳与软件准备度", "/api/simulation/capacity-readiness", simulationAllowed, false, false),
        Stage("S9", "真实 HIL 与试运行", "现场证据、协议、机械安全与 Site Acceptance", "/api/hil/verification", hilAllowed, false, true),
        Stage("S10", "统一验证中心", "S0～S9 能力、状态与安全边界统一展示", "/api/simulation/verification", true, false, false)
    ];

    private static SimulationVerificationStage Stage(
        string id,
        string name,
        string capability,
        string apiPrefix,
        bool available,
        bool requiresRunId,
        bool requiresRealHardware) => new()
        {
            Id = id,
            Name = name,
            Capability = capability,
            ApiPrefix = apiPrefix,
            Availability = available ? "Available" : "UnavailableInCurrentEnvironment",
            ReadOnlyInspection = true,
            RequiresRunId = requiresRunId,
            RequiresRealHardware = requiresRealHardware,
            SafetyBoundary = requiresRealHardware
                ? "真实结果必须来自受控 self-hosted HIL 台架；页面不提供执行或验收入口"
                : "仅查看受控运行时和证据；不得替代生产控制或现场验收"
        };
}

public sealed class SimulationVerificationOverview
{
    public string Stage { get; init; } = "S10";
    public string Environment { get; init; } = string.Empty;
    public bool ReadOnly { get; init; }
    public bool RemoteControlAllowed { get; init; }
    public bool SimulationInspectionAvailable { get; init; }
    public bool HilInspectionAvailable { get; init; }
    public bool RealHilExecuted { get; init; }
    public bool ProtocolValidated { get; init; }
    public bool MechanicalSafetyAccepted { get; init; }
    public bool SiteAccepted { get; init; }
    public bool RealHilEvidenceRequiredForCompletion { get; init; }
    public IReadOnlyList<SimulationVerificationStage> Stages { get; init; } = [];
}

public sealed class SimulationVerificationStage
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string ApiPrefix { get; init; } = string.Empty;
    public string Availability { get; init; } = string.Empty;
    public bool ReadOnlyInspection { get; init; }
    public bool RequiresRunId { get; init; }
    public bool RequiresRealHardware { get; init; }
    public string SafetyBoundary { get; init; } = string.Empty;
}
