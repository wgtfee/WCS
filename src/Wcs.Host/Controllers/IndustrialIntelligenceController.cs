namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.IndustrialIntelligence.Governance;

[ApiController]
[Route("api/industrial-intelligence")]
public sealed class IndustrialIntelligenceController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public IndustrialIntelligenceController(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var decision = GetDecision();
        if (!decision.Allowed)
            return NotFound();

        return Ok(new IndustrialIntelligenceStatusResponse
        {
            Stage = "IDI-P0",
            Environment = _environment.EnvironmentName,
            Enabled = true,
            Mode = decision.EffectiveMode.ToString(),
            MaximumAutomationLevel = decision.EffectiveMaximumAutomationLevel.ToString(),
            ReadOnly = true,
            ControlWriteAllowed = false,
            ProductionAllowed = false,
            EvidenceRequired = true,
            AuditRequired = true
        });
    }

    [HttpGet("capabilities")]
    public IActionResult GetCapabilities()
    {
        var decision = GetDecision();
        if (!decision.Allowed)
            return NotFound();

        return Ok(new IndustrialIntelligenceCapabilitiesResponse
        {
            Stage = "IDI-P0",
            ReadOnly = true,
            ControlWriteAllowed = false,
            Capabilities =
            [
                Capability("GovernanceContracts", true, "版本、Hash、Actor/Reason、有界配置与状态契约"),
                Capability("EvidenceGovernance", true, "SHA-256 Evidence 引用与不可变标识"),
                Capability("AuditJournal", true, "append-only 审计契约"),
                Capability("ModelOps", false, "IDI-P1"),
                Capability("FeatureCenter", false, "IDI-P2"),
                Capability("ShadowDecision", false, "IDI-P3"),
                Capability("MaintenanceLearning", false, "IDI-P4"),
                Capability("DigitalTwinOptimizer", false, "IDI-P5"),
                Capability("BoundedAutomation", false, "IDI-P6 software-side readiness only")
            ]
        });
    }

    private IndustrialIntelligenceAccessDecision GetDecision()
    {
        // Bind into an empty allow-list to avoid ConfigurationBinder array append surprises.
        var options = new IndustrialIntelligenceOptions { AllowedEnvironments = [] };
        _configuration.GetSection(IndustrialIntelligenceOptions.SectionName).Bind(options);
        return IndustrialIntelligenceEnvironmentGuard.Evaluate(_environment.EnvironmentName, options);
    }

    private static IndustrialIntelligenceCapability Capability(
        string name,
        bool available,
        string description) => new()
        {
            Name = name,
            Available = available,
            Description = description
        };
}

public sealed class IndustrialIntelligenceStatusResponse
{
    public string Stage { get; init; } = "IDI-P0";
    public string Environment { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Mode { get; init; } = string.Empty;
    public string MaximumAutomationLevel { get; init; } = string.Empty;
    public bool ReadOnly { get; init; }
    public bool ControlWriteAllowed { get; init; }
    public bool ProductionAllowed { get; init; }
    public bool EvidenceRequired { get; init; }
    public bool AuditRequired { get; init; }
}

public sealed class IndustrialIntelligenceCapabilitiesResponse
{
    public string Stage { get; init; } = "IDI-P0";
    public bool ReadOnly { get; init; }
    public bool ControlWriteAllowed { get; init; }
    public IReadOnlyList<IndustrialIntelligenceCapability> Capabilities { get; init; } = [];
}

public sealed class IndustrialIntelligenceCapability
{
    public string Name { get; init; } = string.Empty;
    public bool Available { get; init; }
    public string Description { get; init; } = string.Empty;
}
