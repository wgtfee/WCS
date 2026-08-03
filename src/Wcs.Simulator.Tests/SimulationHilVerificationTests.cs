namespace Wcs.Simulator.Tests;

using Wcs.Simulator.HilVerification;

public sealed class SimulationHilVerificationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    private const string S8Head = "02b202862816a91ff473925bb964e4d2aa2f6470";
    private const string Bundle = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void DisabledRuntime_FailsClosed()
    {
        var runtime = new HilVerificationRuntime(new HilVerificationOptions { Enabled = false });
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile()));
    }

    [Fact]
    public void HardwareProfile_RejectsProductionNetworkAndCredentials()
    {
        var runtime = Runtime();
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile() with { ProductionNetworkIsolated = false }));
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile() with { UsesProductionCredentials = true }));
    }

    [Fact]
    public void Session_RequiresExactS8EvidenceHeadAndDistinctApprovers()
    {
        var runtime = PreparedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(Manifest() with { S8EvidenceHead = "not-a-sha" }));
        Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(Manifest() with { SafetyApprover = "operator-a" }));
    }

    [Fact]
    public void FailedSafetyPreflight_RejectsSession()
    {
        var runtime = PreparedRuntime();
        runtime.CreateSession(Manifest());
        var snapshot = runtime.SubmitPreflight("HIL-1", Preflight() with { EmergencyStopVerified = false });
        Assert.Equal(HilSessionState.Rejected, snapshot.State);
        Assert.False(snapshot.RealHilExecuted);
    }

    [Fact]
    public void HostedRunner_CannotStartRealHilExecution()
    {
        var runtime = ArmedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation() with { RunnerKind = "GitHubHosted" }));
        Assert.Equal(HilSessionState.Armed, runtime.GetSession("HIL-1").State);
    }

    [Fact]
    public void BenchMismatch_CannotStartExecution()
    {
        var runtime = ArmedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation() with { BenchId = "BENCH-OTHER" }));
    }

    [Fact]
    public void RealHilExecution_RequiresPassingEvidenceForEveryPlanStep()
    {
        var runtime = RunningRuntime();
        runtime.RecordEvidence("HIL-1", Evidence(1, "read-plc", HilStepResult.Passed));
        Assert.Throws<InvalidOperationException>(() => runtime.CompleteExecution("HIL-1"));
        runtime.RecordEvidence("HIL-1", Evidence(2, "move-rgv", HilStepResult.Passed));
        var completed = runtime.CompleteExecution("HIL-1");
        Assert.Equal(HilSessionState.Completed, completed.State);
        Assert.True(completed.RealHilExecuted);
        Assert.False(completed.SiteAccepted);
    }

    [Fact]
    public void FailedStepEvidence_BlocksCompletionEvenIfLaterPassExists()
    {
        var runtime = RunningRuntime();
        runtime.RecordEvidence("HIL-1", Evidence(1, "read-plc", HilStepResult.Passed));
        runtime.RecordEvidence("HIL-1", Evidence(2, "move-rgv", HilStepResult.Failed));
        runtime.RecordEvidence("HIL-1", Evidence(3, "move-rgv", HilStepResult.Passed));
        Assert.Throws<InvalidOperationException>(() => runtime.CompleteExecution("HIL-1"));
    }

    [Fact]
    public void StepEvidence_MustComeFromRealHardware()
    {
        var runtime = RunningRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence(
            "HIL-1", Evidence(1, "read-plc", HilStepResult.Passed) with { RealHardwareObserved = false }));
    }

    [Fact]
    public void Acceptance_RequiresProtocolMechanicalAndSiteApproval()
    {
        var runtime = CompletedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.Accept("HIL-1", Acceptance() with { SiteAccepted = false }));

        var accepted = runtime.Accept("HIL-1", Acceptance());
        Assert.Equal(HilSessionState.Accepted, accepted.State);
        Assert.True(accepted.RealHilExecuted);
        Assert.True(accepted.ProtocolValidated);
        Assert.True(accepted.MechanicalSafetyAccepted);
        Assert.True(accepted.SiteAccepted);
    }

    [Fact]
    public void EvidenceHash_IsStableForEquivalentRealHilEvidence()
    {
        var first = CompletedRuntime();
        var second = CompletedRuntime();
        Assert.Equal(first.GetSession("HIL-1").EvidenceHash, second.GetSession("HIL-1").EvidenceHash);
    }

    [Fact]
    public void SessionEvidence_IsBounded()
    {
        var options = Options();
        options.MaximumEvidenceRecordsPerSession = 1;
        var runtime = PreparedRuntime(options);
        runtime.CreateSession(Manifest());
        runtime.SubmitPreflight("HIL-1", Preflight());
        runtime.Arm("HIL-1");
        runtime.BeginExecution("HIL-1", Attestation());
        runtime.RecordEvidence("HIL-1", Evidence(1, "read-plc", HilStepResult.Passed));
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence("HIL-1", Evidence(2, "move-rgv", HilStepResult.Passed)));
    }

    private static HilVerificationRuntime CompletedRuntime()
    {
        var runtime = RunningRuntime();
        runtime.RecordEvidence("HIL-1", Evidence(1, "read-plc", HilStepResult.Passed));
        runtime.RecordEvidence("HIL-1", Evidence(2, "move-rgv", HilStepResult.Passed));
        runtime.CompleteExecution("HIL-1");
        return runtime;
    }

    private static HilVerificationRuntime RunningRuntime()
    {
        var runtime = ArmedRuntime();
        runtime.BeginExecution("HIL-1", Attestation());
        return runtime;
    }

    private static HilVerificationRuntime ArmedRuntime()
    {
        var runtime = PreparedRuntime();
        runtime.CreateSession(Manifest());
        runtime.SubmitPreflight("HIL-1", Preflight());
        runtime.Arm("HIL-1");
        return runtime;
    }

    private static HilVerificationRuntime PreparedRuntime(HilVerificationOptions? options = null)
    {
        var runtime = Runtime(options);
        runtime.DefineHardwareProfile(Profile());
        runtime.DefinePlan(Plan());
        return runtime;
    }

    private static HilVerificationRuntime Runtime(HilVerificationOptions? options = null) =>
        new(options ?? Options());

    private static HilVerificationOptions Options() => new()
    {
        Enabled = true,
        MaximumHardwareProfiles = 8,
        MaximumPlans = 8,
        MaximumSessions = 8,
        MaximumStepsPerPlan = 16,
        MaximumEvidenceRecordsPerSession = 128,
        MaximumEvidenceValueCharacters = 1024,
        MaximumSessionDurationMinutes = 480,
        RequireDualApproval = true,
        RequireSelfHostedHilRunner = true
    };

    private static HilHardwareProfileDefinition Profile() => new()
    {
        ProfileId = "BENCH-PROFILE-1",
        BenchId = "BENCH-1",
        PlcProtocol = "S7",
        ControllerAssetIds = ["PLC-HIL-1"],
        VehicleAssetIds = ["RGV-HIL-1"],
        ProductionNetworkIsolated = true,
        UsesProductionCredentials = false
    };

    private static HilTrialPlanDefinition Plan() => new()
    {
        PlanId = "PLAN-1",
        Version = "1.0.0",
        Steps =
        [
            new HilTrialStepDefinition
            {
                StepId = "read-plc",
                Kind = HilStepKind.PlcRead,
                AssetId = "PLC-HIL-1",
                ExpectedOutcome = "PLC read is stable and matches the bench fixture.",
                TimeoutSeconds = 30
            },
            new HilTrialStepDefinition
            {
                StepId = "move-rgv",
                Kind = HilStepKind.VehicleMove,
                AssetId = "RGV-HIL-1",
                ExpectedOutcome = "RGV performs the approved bench movement and stops at the expected sensor.",
                TimeoutSeconds = 60,
                RequiresMotion = true
            }
        ]
    };

    private static HilSessionManifest Manifest() => new()
    {
        SessionId = "HIL-1",
        S8EvidenceHead = S8Head,
        HardwareProfileId = "BENCH-PROFILE-1",
        PlanId = "PLAN-1",
        ChangeTicket = "CHG-20260803-001",
        MaintenanceWindowId = "MW-20260803-001",
        Operator = "operator-a",
        SafetyApprover = "safety-b",
        CreatedAtUtc = Start
    };

    private static HilSafetyPreflight Preflight() => new()
    {
        EmergencyStopVerified = true,
        MechanicalInterlocksVerified = true,
        GuardingVerified = true,
        NetworkIsolationVerified = true,
        MaintenanceModeVerified = true,
        OperatorAreaClear = true,
        Operator = "operator-a",
        SafetyApprover = "safety-b",
        VerifiedAtUtc = Start.AddMinutes(5)
    };

    private static HilExecutionAttestation Attestation() => new()
    {
        RunnerKind = "SelfHostedHil",
        RunnerName = "hil-runner-01",
        BenchId = "BENCH-1",
        RealHardwareConnected = true,
        StartedAtUtc = Start.AddMinutes(10),
        EvidenceBundleSha256 = Bundle
    };

    private static HilEvidenceRecord Evidence(long sequence, string stepId, HilStepResult result) => new()
    {
        Sequence = sequence,
        OccurredAtUtc = Start.AddMinutes(10).AddSeconds(sequence),
        Kind = HilEvidenceKind.StepResult,
        StepId = stepId,
        AssetId = stepId == "read-plc" ? "PLC-HIL-1" : "RGV-HIL-1",
        Result = result,
        Value = result.ToString(),
        RealHardwareObserved = true
    };

    private static HilAcceptanceRequest Acceptance() => new()
    {
        ProtocolValidated = true,
        MechanicalSafetyAccepted = true,
        SiteAccepted = true,
        AcceptedBy = "site-acceptor-c",
        AcceptedAtUtc = Start.AddHours(1),
        EvidenceBundleSha256 = Bundle
    };
}
