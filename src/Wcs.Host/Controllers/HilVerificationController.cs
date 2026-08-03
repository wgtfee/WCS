namespace Wcs.Host.Controllers;

using Microsoft.AspNetCore.Mvc;
using Wcs.Simulator.HilVerification;

/// <summary>
/// Read-only S9 inspection surface. This controller cannot arm, start, abort, recover,
/// accept, or otherwise control a real HIL session.
/// </summary>
[ApiController]
[Route("api/hil/verification")]
public sealed class HilVerificationController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public HilVerificationController(IHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var (decision, options) = GetAccessDecision();
        if (!decision.Allowed) return NotFound();

        return Ok(new
        {
            stage = "S9",
            environment = _environment.EnvironmentName,
            enabled = options.Enabled,
            readOnly = true,
            remoteControlAllowed = false,
            hostedCiCanExecuteRealHil = false,
            externalSelfHostedHilRunnerRequired = options.RequireSelfHostedHilRunner,
            dualApprovalRequired = options.RequireDualApproval,
            realHilExecuted = false,
            protocolValidated = false,
            mechanicalSafetyAccepted = false,
            siteAccepted = false,
            acceptanceEvidenceRequired = true,
            allowedEnvironments = options.AllowedEnvironments,
            options.MaximumHardwareProfiles,
            options.MaximumPlans,
            options.MaximumSessions,
            options.MaximumStepsPerPlan,
            options.MaximumEvidenceRecordsPerSession,
            options.MaximumSessionDurationMinutes
        });
    }

    [HttpGet("acceptance-requirements")]
    public IActionResult GetAcceptanceRequirements()
    {
        var (decision, _) = GetAccessDecision();
        if (!decision.Allowed) return NotFound();

        return Ok(new
        {
            requiredRunnerKind = "SelfHostedHil",
            requiredRunnerLabels = new[] { "self-hosted", "wcs-hil" },
            realHardwareConnected = true,
            productionNetworkIsolated = true,
            productionCredentialsAllowed = false,
            everyPlanStepRequiresRealHardwareObserved = true,
            abortRequiresRecoveryBeforeReplacementSession = true,
            sameAbortedSessionMayResume = false,
            protocolValidatedRequired = true,
            mechanicalSafetyAcceptedRequired = true,
            siteAcceptedRequired = true,
            evidenceBundleSha256Required = true,
            controlEndpointsExposed = false
        });
    }

    private (HilEnvironmentAccessDecision Decision, HilVerificationOptions Options) GetAccessDecision()
    {
        // ConfigurationBinder appends array values to initialized arrays. Start the bind target
        // with an empty allow-list so HIL/TrialRun are not duplicated and rejected by validation.
        // A missing section remains fail-closed because Enabled=false and the allow-list is empty.
        var options = new HilVerificationOptions { AllowedEnvironments = [] };
        _configuration.GetSection(HilVerificationOptions.SectionName).Bind(options);
        return (HilEnvironmentBoundaryGuard.Evaluate(_environment.EnvironmentName, options), options);
    }
}
