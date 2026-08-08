namespace Wcs.Simulator.Tests;

using Wcs.IndustrialIntelligence.Governance;

public sealed class BoundedAutomationReadinessTests
{
    private const string GitSha40 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string GitSha64 = new('b', 64);

    [Fact]
    public void NullRequest_IsFailClosed()
    {
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(null);
        Assert.False(decision.SoftwareSideReady);
        Assert.False(decision.ProductionEnablementAllowed);
        Assert.Equal(AutomationLevel.L0, decision.EffectiveMaximumAutomationLevel);
    }

    [Fact]
    public void GovernanceClaim_IsSoftwareSideOnly()
    {
        Assert.Equal("software-side ready only", BoundedAutomationReadinessGovernance.SoftwareOnlyClaim);
        Assert.False(BoundedAutomationReadinessGovernance.ProductionEnablementAllowed);
    }

    [Fact]
    public void AutomationPolicy_Default_IsDisabled()
    {
        Assert.False(AutomationPolicy.Disabled.Enabled);
        Assert.Equal(AutomationLevel.L0, AutomationPolicy.Disabled.RequestedLevel);
        Assert.True(Hashing.IsSha256(AutomationPolicy.Disabled.PolicyHash));
    }

    [Fact]
    public void ExecutionAllowance_Default_IsDenied()
    {
        Assert.False(ExecutionAllowance.Disabled.Enabled);
        Assert.Equal(ExecutionAllowanceKind.Denied, ExecutionAllowance.Disabled.Kind);
    }

    [Fact]
    public void RateAndBudget_Defaults_AreDisabledAndZero()
    {
        Assert.False(RateLimit.Disabled.Enabled);
        Assert.Equal(0, RateLimit.Disabled.MaximumOperationsPerMinute);
        Assert.False(BudgetLimit.Disabled.Enabled);
        Assert.Equal(0m, BudgetLimit.Disabled.MaximumCostUnitsPerHour);
    }

    [Fact]
    public void MaintenanceAndApproval_Defaults_AreDisabled()
    {
        Assert.False(MaintenanceWindow.Disabled.Enabled);
        Assert.False(ApprovalRequirement.Disabled.Enabled);
        Assert.Equal(0, ApprovalRequirement.Disabled.RequiredApprovals);
    }

    [Fact]
    public void BreakerKillSwitchAndRollback_Defaults_AreDisabled()
    {
        Assert.False(CircuitBreaker.Disabled.Enabled);
        Assert.False(KillSwitch.Disabled.Enabled);
        Assert.False(KillSwitch.Disabled.Armed);
        Assert.False(RollbackPolicy.Disabled.Enabled);
    }

    [Fact]
    public void BlankEnvironment_IsDenied()
    {
        AssertDenied(ValidRequest() with { EnvironmentName = " " }, "environment name");
    }

    [Fact]
    public void DisabledAutomationPolicy_IsDenied()
    {
        AssertDenied(ValidRequest() with { AutomationPolicy = AutomationPolicy.Disabled }, "AutomationPolicy is Disabled");
    }

    [Fact]
    public void BlankPolicyVersion_IsDenied()
    {
        var request = ValidRequest();
        AssertDenied(request with { AutomationPolicy = request.AutomationPolicy with { PolicyVersion = "" } }, "version/hash");
    }

    [Fact]
    public void InvalidPolicyHash_IsDenied()
    {
        var request = ValidRequest();
        AssertDenied(request with { AutomationPolicy = request.AutomationPolicy with { PolicyHash = "bad" } }, "version/hash");
    }

    [Fact]
    public void DisabledExecutionAllowance_IsDenied()
    {
        AssertDenied(ValidRequest() with { ExecutionAllowance = ExecutionAllowance.Disabled }, "ExecutionAllowance is Disabled");
    }

    [Fact]
    public void UnknownExecutionAllowanceKind_IsDenied()
    {
        AssertDenied(ValidRequest() with { ExecutionAllowance = new ExecutionAllowance(true, (ExecutionAllowanceKind)999) }, "kind is invalid");
    }

    [Fact]
    public void ZeroRateLimit_IsDenied()
    {
        AssertDenied(ValidRequest() with { RateLimit = new RateLimit(true, 0) }, "RateLimit");
    }

    [Fact]
    public void ZeroBudgetLimit_IsDenied()
    {
        AssertDenied(ValidRequest() with { BudgetLimit = new BudgetLimit(true, 0m) }, "BudgetLimit");
    }

    [Fact]
    public void DisabledMaintenanceWindow_IsDenied()
    {
        AssertDenied(ValidRequest() with { MaintenanceWindow = MaintenanceWindow.Disabled }, "MaintenanceWindow");
    }

    [Fact]
    public void EqualMaintenanceWindowBounds_AreDenied()
    {
        AssertDenied(ValidRequest() with { MaintenanceWindow = new MaintenanceWindow(true, TimeSpan.FromHours(1), TimeSpan.FromHours(1)) }, "MaintenanceWindow");
    }

    [Fact]
    public void MaintenanceWindowStartOutsideDay_IsDenied()
    {
        AssertDenied(ValidRequest() with { MaintenanceWindow = new MaintenanceWindow(true, TimeSpan.FromHours(24), TimeSpan.FromHours(2)) }, "MaintenanceWindow");
    }

    [Fact]
    public void MaintenanceWindowEndOutsideDay_IsDenied()
    {
        AssertDenied(ValidRequest() with { MaintenanceWindow = new MaintenanceWindow(true, TimeSpan.FromHours(1), TimeSpan.FromHours(24)) }, "MaintenanceWindow");
    }

    [Fact]
    public void DisabledApprovalRequirement_IsDenied()
    {
        AssertDenied(ValidRequest() with { ApprovalRequirement = ApprovalRequirement.Disabled }, "ApprovalRequirement");
    }

    [Fact]
    public void ZeroRequiredApprovals_IsDenied()
    {
        AssertDenied(ValidRequest() with { ApprovalRequirement = new ApprovalRequirement(true, 0, true) }, "ApprovalRequirement");
    }

    [Fact]
    public void MissingIndependentSafetyApprovalRequirement_IsDenied()
    {
        AssertDenied(ValidRequest() with { ApprovalRequirement = new ApprovalRequirement(true, 2, false) }, "ApprovalRequirement");
    }

    [Fact]
    public void DisabledCircuitBreaker_IsDenied()
    {
        AssertDenied(ValidRequest() with { CircuitBreaker = CircuitBreaker.Disabled }, "CircuitBreaker");
    }

    [Fact]
    public void ZeroCircuitBreakerThreshold_IsDenied()
    {
        AssertDenied(ValidRequest() with { CircuitBreaker = new CircuitBreaker(true, 0, TimeSpan.FromMinutes(1)) }, "CircuitBreaker");
    }

    [Fact]
    public void ZeroCircuitBreakerOpenDuration_IsDenied()
    {
        AssertDenied(ValidRequest() with { CircuitBreaker = new CircuitBreaker(true, 3, TimeSpan.Zero) }, "CircuitBreaker");
    }

    [Fact]
    public void DisabledKillSwitch_IsDenied()
    {
        AssertDenied(ValidRequest() with { KillSwitch = KillSwitch.Disabled }, "KillSwitch");
    }

    [Fact]
    public void UnarmedKillSwitch_IsDenied()
    {
        AssertDenied(ValidRequest() with { KillSwitch = new KillSwitch(true, false) }, "KillSwitch");
    }

    [Fact]
    public void DisabledRollbackPolicy_IsDenied()
    {
        AssertDenied(ValidRequest() with { RollbackPolicy = RollbackPolicy.Disabled }, "RollbackPolicy");
    }

    [Fact]
    public void MissingRollbackTarget_IsDenied()
    {
        AssertDenied(ValidRequest() with { RollbackPolicy = new RollbackPolicy(true, "", TimeSpan.FromMinutes(5)) }, "RollbackPolicy");
    }

    [Fact]
    public void ZeroRollbackDuration_IsDenied()
    {
        AssertDenied(ValidRequest() with { RollbackPolicy = new RollbackPolicy(true, "v-prev", TimeSpan.Zero) }, "RollbackPolicy");
    }

    [Fact]
    public void InvalidSoftwareEvidenceFlag_IsDenied()
    {
        var request = ValidRequest();
        AssertDenied(request with { Evidence = request.Evidence with { SoftwareEvidenceValid = false } }, "software Evidence");
    }

    [Fact]
    public void InvalidGitCommitSha_IsDenied()
    {
        var request = ValidRequest();
        AssertDenied(request with { Evidence = request.Evidence with { SoftwareHeadSha = "deadbeef" } }, "software Evidence");
    }

    [Fact]
    public void InvalidEvidenceHash_IsDenied()
    {
        var request = ValidRequest();
        AssertDenied(request with { Evidence = request.Evidence with { EvidenceHash = "bad" } }, "software Evidence");
    }

    [Fact]
    public void ValidL1SoftwareRequest_IsReadyButNeverProductionEnabled()
    {
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(ValidRequest());
        Assert.True(decision.SoftwareSideReady);
        Assert.False(decision.ProductionEnablementAllowed);
        Assert.Equal(AutomationLevel.L1, decision.EffectiveMaximumAutomationLevel);
        Assert.Equal("software-side ready only", decision.Claim);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void L2WithoutRealEvidence_IsDeniedWithAllRequiredReasons()
    {
        var request = ValidRequest(AutomationLevel.L2);
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        Assert.False(decision.SoftwareSideReady);
        Assert.Contains(decision.Reasons, x => x.Contains("real site Evidence", StringComparison.Ordinal));
        Assert.Contains(decision.Reasons, x => x.Contains("real HIL Evidence", StringComparison.Ordinal));
        Assert.Contains(decision.Reasons, x => x.Contains("safety approval Evidence", StringComparison.Ordinal));
        Assert.Contains(decision.Reasons, x => x.Contains("rollback Evidence", StringComparison.Ordinal));
        Assert.False(decision.ProductionEnablementAllowed);
    }

    [Fact]
    public void L2WithAllEvidence_CanOnlyBecomeSoftwareSideReady()
    {
        var request = WithRealEvidence(ValidRequest(AutomationLevel.L2));
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        Assert.True(decision.SoftwareSideReady);
        Assert.Equal(AutomationLevel.L2, decision.EffectiveMaximumAutomationLevel);
        Assert.False(decision.ProductionEnablementAllowed);
        Assert.Equal("software-side ready only", decision.Claim);
    }

    [Fact]
    public void ProductionEnvironment_WithAllEvidence_StillCannotEnableProduction()
    {
        var request = WithRealEvidence(ValidRequest(AutomationLevel.L2)) with { EnvironmentName = "Production" };
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        Assert.True(decision.SoftwareSideReady);
        Assert.False(decision.ProductionEnablementAllowed);
        Assert.Equal("software-side ready only", decision.Claim);
    }

    [Fact]
    public void PermanentProhibitionCatalog_IsCompleteAndEveryEntryIsDenied()
    {
        var expected = Enum.GetValues<PermanentAutomationProhibition>();
        Assert.Equal(11, expected.Length);
        Assert.Equal(expected.OrderBy(x => x), BoundedAutomationReadinessGovernance.PermanentProhibitions.OrderBy(x => x));

        foreach (var prohibited in expected)
        {
            var decision = BoundedAutomationReadinessEvaluator.Evaluate(
                ValidRequest() with { RequestedProhibitedOperations = new[] { prohibited } });
            Assert.False(decision.SoftwareSideReady);
            Assert.Contains(decision.Reasons, x => x.Contains(prohibited.ToString(), StringComparison.Ordinal));
            Assert.False(decision.ProductionEnablementAllowed);
        }
    }

    [Fact]
    public void L4_IsOutsideP6EvaluationBoundary()
    {
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(WithRealEvidence(ValidRequest(AutomationLevel.L4)));
        Assert.False(decision.SoftwareSideReady);
        Assert.Contains(decision.Reasons, x => x.Contains("above L3", StringComparison.Ordinal));
        Assert.False(decision.ProductionEnablementAllowed);
    }

    [Fact]
    public void GitShaValidator_AcceptsCurrentAndFutureGitWidths()
    {
        Assert.True(BoundedAutomationReadinessGovernance.IsGitCommitSha(GitSha40));
        Assert.True(BoundedAutomationReadinessGovernance.IsGitCommitSha(GitSha64));
    }

    [Fact]
    public void P0ProductionGuard_RemainsFailClosed()
    {
        var options = ValidP0Options();
        options.AllowedEnvironments = ["Production"];
        var decision = IndustrialIntelligenceEnvironmentGuard.Evaluate("Production", options);
        Assert.False(decision.Allowed);
        Assert.Equal(AutomationLevel.L0, decision.EffectiveMaximumAutomationLevel);
        Assert.Contains("Production is fail-closed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void P0Guard_StillRejectsConfiguredL2()
    {
        var options = ValidP0Options();
        options.MaximumAutomationLevel = AutomationLevel.L2;
        var decision = IndustrialIntelligenceEnvironmentGuard.Evaluate("IndustrialIntelligence", options);
        Assert.False(decision.Allowed);
        Assert.Contains("L0/L1", decision.Reason, StringComparison.Ordinal);
    }

    private static BoundedAutomationReadinessRequest ValidRequest(AutomationLevel level = AutomationLevel.L1) => new(
        EnvironmentName: "IndustrialIntelligence",
        AutomationPolicy: new AutomationPolicy(true, level, "p6-v1", Hashing.Sha256($"p6-policy:{level}")),
        ExecutionAllowance: new ExecutionAllowance(true, ExecutionAllowanceKind.SoftwareSimulation),
        RateLimit: new RateLimit(true, 60),
        BudgetLimit: new BudgetLimit(true, 100m),
        MaintenanceWindow: new MaintenanceWindow(true, TimeSpan.FromHours(1), TimeSpan.FromHours(2)),
        ApprovalRequirement: new ApprovalRequirement(true, 2, true),
        CircuitBreaker: new CircuitBreaker(true, 3, TimeSpan.FromMinutes(1)),
        KillSwitch: new KillSwitch(true, true),
        RollbackPolicy: new RollbackPolicy(true, "p6-v0", TimeSpan.FromMinutes(5)),
        Evidence: new BoundedAutomationEvidence(
            SoftwareEvidenceValid: true,
            SiteEvidenceValid: false,
            HilEvidenceValid: false,
            SafetyApprovalEvidenceValid: false,
            RollbackEvidenceValid: false,
            SoftwareHeadSha: GitSha40,
            EvidenceHash: Hashing.Sha256("p6-software-evidence")),
        RequestedProhibitedOperations: Array.Empty<PermanentAutomationProhibition>());

    private static BoundedAutomationReadinessRequest WithRealEvidence(BoundedAutomationReadinessRequest request) =>
        request with
        {
            Evidence = request.Evidence with
            {
                SiteEvidenceValid = true,
                HilEvidenceValid = true,
                SafetyApprovalEvidenceValid = true,
                RollbackEvidenceValid = true
            }
        };

    private static IndustrialIntelligenceOptions ValidP0Options() => new()
    {
        Enabled = true,
        Mode = IndustrialIntelligenceMode.ReadOnly,
        AllowedEnvironments = ["IndustrialIntelligence"],
        MaximumAutomationLevel = AutomationLevel.L1
    };

    private static void AssertDenied(BoundedAutomationReadinessRequest request, string reasonFragment)
    {
        var decision = BoundedAutomationReadinessEvaluator.Evaluate(request);
        Assert.False(decision.SoftwareSideReady);
        Assert.False(decision.ProductionEnablementAllowed);
        Assert.Equal(AutomationLevel.L0, decision.EffectiveMaximumAutomationLevel);
        Assert.Contains(decision.Reasons, reason => reason.Contains(reasonFragment, StringComparison.Ordinal));
    }
}
