namespace Wcs.IndustrialIntelligence.Governance;

public enum ExecutionAllowanceKind
{
    Denied = 0,
    SoftwareSimulation = 1,
    ShadowObservation = 2
}

public enum PermanentAutomationProhibition
{
    EmergencyStop = 0,
    SafetyReset = 1,
    SafetyDoorBypass = 2,
    LightCurtainBypass = 3,
    MechanicalInterlockBypass = 4,
    PlcForceWrite = 5,
    AutomaticRoadRightRelease = 6,
    AutomaticBlockRelease = 7,
    UnapprovedShutdown = 8,
    StateMachineBypass = 9,
    TrafficConstraintBypass = 10
}

public sealed record AutomationPolicy(
    bool Enabled,
    AutomationLevel RequestedLevel,
    string PolicyVersion,
    string PolicyHash)
{
    public static AutomationPolicy Disabled { get; } = new(
        false,
        AutomationLevel.L0,
        "disabled",
        Hashing.Sha256("idi-p6:automation-policy:disabled"));
}

public sealed record ExecutionAllowance(bool Enabled, ExecutionAllowanceKind Kind)
{
    public static ExecutionAllowance Disabled { get; } = new(false, ExecutionAllowanceKind.Denied);
}

public sealed record RateLimit(bool Enabled, int MaximumOperationsPerMinute)
{
    public static RateLimit Disabled { get; } = new(false, 0);
}

public sealed record BudgetLimit(bool Enabled, decimal MaximumCostUnitsPerHour)
{
    public static BudgetLimit Disabled { get; } = new(false, 0m);
}

public sealed record MaintenanceWindow(bool Enabled, TimeSpan StartUtc, TimeSpan EndUtc)
{
    public static MaintenanceWindow Disabled { get; } = new(false, TimeSpan.Zero, TimeSpan.Zero);
}

public sealed record ApprovalRequirement(
    bool Enabled,
    int RequiredApprovals,
    bool IndependentSafetyApprovalRequired)
{
    public static ApprovalRequirement Disabled { get; } = new(false, 0, true);
}

public sealed record CircuitBreaker(bool Enabled, int FailureThreshold, TimeSpan OpenDuration)
{
    public static CircuitBreaker Disabled { get; } = new(false, 0, TimeSpan.Zero);
}

public sealed record KillSwitch(bool Enabled, bool Armed)
{
    public static KillSwitch Disabled { get; } = new(false, false);
}

public sealed record RollbackPolicy(bool Enabled, string? TargetVersion, TimeSpan MaximumRollbackDuration)
{
    public static RollbackPolicy Disabled { get; } = new(false, null, TimeSpan.Zero);
}

public sealed record BoundedAutomationEvidence(
    bool SoftwareEvidenceValid,
    bool SiteEvidenceValid,
    bool HilEvidenceValid,
    bool SafetyApprovalEvidenceValid,
    bool RollbackEvidenceValid,
    string SoftwareHeadSha,
    string EvidenceHash)
{
    public static BoundedAutomationEvidence None { get; } = new(
        false,
        false,
        false,
        false,
        false,
        string.Empty,
        Hashing.Sha256("idi-p6:evidence:none"));
}

public sealed record BoundedAutomationReadinessRequest(
    string EnvironmentName,
    AutomationPolicy AutomationPolicy,
    ExecutionAllowance ExecutionAllowance,
    RateLimit RateLimit,
    BudgetLimit BudgetLimit,
    MaintenanceWindow MaintenanceWindow,
    ApprovalRequirement ApprovalRequirement,
    CircuitBreaker CircuitBreaker,
    KillSwitch KillSwitch,
    RollbackPolicy RollbackPolicy,
    BoundedAutomationEvidence Evidence,
    IReadOnlyCollection<PermanentAutomationProhibition> RequestedProhibitedOperations);

public sealed record BoundedAutomationReadinessDecision(
    bool SoftwareSideReady,
    bool ProductionEnablementAllowed,
    AutomationLevel EffectiveMaximumAutomationLevel,
    string Claim,
    IReadOnlyList<string> Reasons);

public static class BoundedAutomationReadinessEvaluator
{
    public const string SoftwareOnlyClaim = "software-side ready only";

    public static BoundedAutomationReadinessDecision Evaluate(BoundedAutomationReadinessRequest? request)
    {
        if (request is null)
            return Denied("readiness request is required");

        var reasons = new List<string>();
        var policy = request.AutomationPolicy ?? AutomationPolicy.Disabled;
        var allowance = request.ExecutionAllowance ?? ExecutionAllowance.Disabled;
        var rateLimit = request.RateLimit ?? RateLimit.Disabled;
        var budgetLimit = request.BudgetLimit ?? BudgetLimit.Disabled;
        var maintenanceWindow = request.MaintenanceWindow ?? MaintenanceWindow.Disabled;
        var approval = request.ApprovalRequirement ?? ApprovalRequirement.Disabled;
        var circuitBreaker = request.CircuitBreaker ?? CircuitBreaker.Disabled;
        var killSwitch = request.KillSwitch ?? KillSwitch.Disabled;
        var rollback = request.RollbackPolicy ?? RollbackPolicy.Disabled;
        var evidence = request.Evidence ?? BoundedAutomationEvidence.None;

        if (!policy.Enabled)
            reasons.Add("AutomationPolicy is Disabled");
        if (!allowance.Enabled || allowance.Kind is ExecutionAllowanceKind.Denied)
            reasons.Add("ExecutionAllowance is Disabled");
        if (!rateLimit.Enabled || rateLimit.MaximumOperationsPerMinute <= 0)
            reasons.Add("RateLimit is not configured");
        if (!budgetLimit.Enabled || budgetLimit.MaximumCostUnitsPerHour <= 0m)
            reasons.Add("BudgetLimit is not configured");
        if (!maintenanceWindow.Enabled || maintenanceWindow.StartUtc < TimeSpan.Zero || maintenanceWindow.EndUtc > TimeSpan.FromHours(24))
            reasons.Add("MaintenanceWindow is not configured");
        if (!approval.Enabled || approval.RequiredApprovals <= 0 || !approval.IndependentSafetyApprovalRequired)
            reasons.Add("ApprovalRequirement is not satisfied");
        if (!circuitBreaker.Enabled || circuitBreaker.FailureThreshold <= 0 || circuitBreaker.OpenDuration <= TimeSpan.Zero)
            reasons.Add("CircuitBreaker is not armed by policy");
        if (!killSwitch.Enabled || !killSwitch.Armed)
            reasons.Add("KillSwitch is not enabled and armed");
        if (!rollback.Enabled || string.IsNullOrWhiteSpace(rollback.TargetVersion) || rollback.MaximumRollbackDuration <= TimeSpan.Zero)
            reasons.Add("RollbackPolicy is not configured");
        if (!evidence.SoftwareEvidenceValid || !Hashing.IsSha256(evidence.SoftwareHeadSha) || !Hashing.IsSha256(evidence.EvidenceHash))
            reasons.Add("software Evidence is missing or invalid");

        if (request.RequestedProhibitedOperations is { Count: > 0 })
        {
            foreach (var prohibited in request.RequestedProhibitedOperations.Distinct())
                reasons.Add($"permanent prohibition requested: {prohibited}");
        }

        var productionLevelRequested = policy.RequestedLevel >= AutomationLevel.L2 ||
                                       string.Equals(request.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase);

        if (productionLevelRequested)
        {
            if (!evidence.SiteEvidenceValid)
                reasons.Add("real site Evidence is required for Production L2/L3 readiness");
            if (!evidence.HilEvidenceValid)
                reasons.Add("real HIL Evidence is required for Production L2/L3 readiness");
            if (!evidence.SafetyApprovalEvidenceValid)
                reasons.Add("independent safety approval Evidence is required for Production L2/L3 readiness");
            if (!evidence.RollbackEvidenceValid)
                reasons.Add("verified rollback Evidence is required for Production L2/L3 readiness");
        }

        if (policy.RequestedLevel > AutomationLevel.L3)
            reasons.Add("IDI-P6 does not evaluate automation above L3");

        var softwareSideReady = reasons.Count == 0;
        var effectiveLevel = softwareSideReady
            ? policy.RequestedLevel > AutomationLevel.L3 ? AutomationLevel.L0 : policy.RequestedLevel
            : AutomationLevel.L0;

        // P6 is a readiness evaluator only. It never grants production control authority.
        // Existing P0 production fail-closed governance remains authoritative and unchanged.
        return new BoundedAutomationReadinessDecision(
            softwareSideReady,
            ProductionEnablementAllowed: false,
            effectiveLevel,
            SoftwareOnlyClaim,
            reasons);
    }

    private static BoundedAutomationReadinessDecision Denied(string reason) => new(
        SoftwareSideReady: false,
        ProductionEnablementAllowed: false,
        EffectiveMaximumAutomationLevel: AutomationLevel.L0,
        Claim: SoftwareOnlyClaim,
        Reasons: new[] { reason });
}
