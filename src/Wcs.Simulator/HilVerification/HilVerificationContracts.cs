namespace Wcs.Simulator.HilVerification;

/// <summary>
/// S9 real-HIL verification governance. This module records and verifies HIL evidence;
/// it does not itself open PLC/RGV/network connections or bypass production safety controls.
/// </summary>
public sealed class HilVerificationOptions
{
    public const string SectionName = "HilVerification";

    public bool Enabled { get; set; }
    public int MaximumHardwareProfiles { get; set; } = 32;
    public int MaximumPlans { get; set; } = 128;
    public int MaximumSessions { get; set; } = 256;
    public int MaximumStepsPerPlan { get; set; } = 256;
    public int MaximumEvidenceRecordsPerSession { get; set; } = 10_000;
    public int MaximumEvidenceValueCharacters { get; set; } = 8_192;
    public int MaximumSessionDurationMinutes { get; set; } = 480;
    public bool RequireDualApproval { get; set; } = true;
    public bool RequireSelfHostedHilRunner { get; set; } = true;

    public void Validate()
    {
        if (MaximumHardwareProfiles is < 1 or > 1_000)
            throw new InvalidOperationException("HilVerification.MaximumHardwareProfiles must be between 1 and 1,000.");
        if (MaximumPlans is < 1 or > 10_000)
            throw new InvalidOperationException("HilVerification.MaximumPlans must be between 1 and 10,000.");
        if (MaximumSessions is < 1 or > 100_000)
            throw new InvalidOperationException("HilVerification.MaximumSessions must be between 1 and 100,000.");
        if (MaximumStepsPerPlan is < 1 or > 10_000)
            throw new InvalidOperationException("HilVerification.MaximumStepsPerPlan must be between 1 and 10,000.");
        if (MaximumEvidenceRecordsPerSession is < 1 or > 1_000_000)
            throw new InvalidOperationException("HilVerification.MaximumEvidenceRecordsPerSession must be between 1 and 1,000,000.");
        if (MaximumEvidenceValueCharacters is < 64 or > 1_000_000)
            throw new InvalidOperationException("HilVerification.MaximumEvidenceValueCharacters must be between 64 and 1,000,000.");
        if (MaximumSessionDurationMinutes is < 1 or > 10_080)
            throw new InvalidOperationException("HilVerification.MaximumSessionDurationMinutes must be between 1 minute and 7 days.");
    }
}

public enum HilSessionState
{
    Defined = 0,
    PreflightPassed = 1,
    Armed = 2,
    Running = 3,
    Completed = 4,
    Accepted = 5,
    Aborted = 6,
    Rejected = 7
}

public enum HilStepKind
{
    ConnectivityRead = 0,
    PlcRead = 1,
    ControlledPlcWrite = 2,
    VehicleMove = 3,
    InterlockVerify = 4,
    EmergencyStopVerify = 5,
    RecoveryVerify = 6,
    ExternalAckVerify = 7
}

public enum HilEvidenceKind
{
    Preflight = 0,
    StepResult = 1,
    Telemetry = 2,
    Safety = 3,
    Operator = 4,
    System = 5
}

public enum HilStepResult
{
    Observed = 0,
    Passed = 1,
    Failed = 2
}

public sealed record HilHardwareProfileDefinition
{
    public string ProfileId { get; init; } = string.Empty;
    public string BenchId { get; init; } = string.Empty;
    public string PlcProtocol { get; init; } = string.Empty;
    public IReadOnlyList<string> ControllerAssetIds { get; init; } = [];
    public IReadOnlyList<string> VehicleAssetIds { get; init; } = [];
    public bool ProductionNetworkIsolated { get; init; }
    public bool UsesProductionCredentials { get; init; }
}

public sealed record HilTrialStepDefinition
{
    public string StepId { get; init; } = string.Empty;
    public HilStepKind Kind { get; init; }
    public string AssetId { get; init; } = string.Empty;
    public string ExpectedOutcome { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 30;
    public bool RequiresMotion { get; init; }
    public bool RequiresControlWrite { get; init; }
}

public sealed record HilTrialPlanDefinition
{
    public string PlanId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public IReadOnlyList<HilTrialStepDefinition> Steps { get; init; } = [];
}

public sealed record HilSessionManifest
{
    public string SessionId { get; init; } = string.Empty;
    public string S8EvidenceHead { get; init; } = string.Empty;
    public string HardwareProfileId { get; init; } = string.Empty;
    public string PlanId { get; init; } = string.Empty;
    public string ChangeTicket { get; init; } = string.Empty;
    public string MaintenanceWindowId { get; init; } = string.Empty;
    public string Operator { get; init; } = string.Empty;
    public string SafetyApprover { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
}

public sealed record HilSafetyPreflight
{
    public bool EmergencyStopVerified { get; init; }
    public bool MechanicalInterlocksVerified { get; init; }
    public bool GuardingVerified { get; init; }
    public bool NetworkIsolationVerified { get; init; }
    public bool MaintenanceModeVerified { get; init; }
    public bool OperatorAreaClear { get; init; }
    public string Operator { get; init; } = string.Empty;
    public string SafetyApprover { get; init; } = string.Empty;
    public DateTimeOffset VerifiedAtUtc { get; init; }
}

public sealed record HilExecutionAttestation
{
    public string RunnerKind { get; init; } = string.Empty;
    public string RunnerName { get; init; } = string.Empty;
    public string BenchId { get; init; } = string.Empty;
    public bool RealHardwareConnected { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public string EvidenceBundleSha256 { get; init; } = string.Empty;
}

public sealed record HilEvidenceRecord
{
    public long Sequence { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public HilEvidenceKind Kind { get; init; }
    public string StepId { get; init; } = string.Empty;
    public string AssetId { get; init; } = string.Empty;
    public HilStepResult Result { get; init; }
    public string Value { get; init; } = string.Empty;
    public bool RealHardwareObserved { get; init; }
}

public sealed record HilAcceptanceRequest
{
    public bool ProtocolValidated { get; init; }
    public bool MechanicalSafetyAccepted { get; init; }
    public bool SiteAccepted { get; init; }
    public string AcceptedBy { get; init; } = string.Empty;
    public DateTimeOffset AcceptedAtUtc { get; init; }
    public string EvidenceBundleSha256 { get; init; } = string.Empty;
}

public sealed record HilSessionSnapshot(
    string SessionId,
    HilSessionState State,
    string HardwareProfileId,
    string PlanId,
    string S8EvidenceHead,
    bool RealHilExecuted,
    bool ProtocolValidated,
    bool MechanicalSafetyAccepted,
    bool SiteAccepted,
    int EvidenceCount,
    string EvidenceHash,
    string? Detail);
