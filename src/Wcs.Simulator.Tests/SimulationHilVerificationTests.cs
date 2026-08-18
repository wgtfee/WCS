namespace Wcs.Simulator.Tests;

using Wcs.Simulator.HilVerification;

public sealed class SimulationHilVerificationTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    private const string S8Head = "02b202862816a91ff473925bb964e4d2aa2f6470";
    private const string SoftwareHead = "1111111111111111111111111111111111111111";
    private const string Bundle = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EvidenceDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ProtocolDigest = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string MechanicalDigest = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string SiteDigest = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Fact]
    public void DisabledRuntime_FailsClosed()
    {
        var runtime = new HilVerificationRuntime(new HilVerificationOptions { Enabled = false });
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile()));
    }

    [Fact]
    public void Options_RejectProductionEnvironment()
    {
        var options = Options();
        options.AllowedEnvironments = ["HIL", "Production"];
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void HardwareProfile_RejectsProductionNetworkAndCredentials()
    {
        var runtime = Runtime();
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile() with { ProductionNetworkIsolated = false }));
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile() with { UsesProductionCredentials = true }));
    }

    [Fact]
    public void HardwareProfile_RequiresApprovalAndTopology()
    {
        var runtime = Runtime();
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile() with { ApprovedBy = "" }));
        Assert.Throws<InvalidOperationException>(() => runtime.DefineHardwareProfile(Profile() with { TopologyRevision = "" }));
    }

    [Fact]
    public void Session_RequiresExactEvidenceHeadsAndDistinctApprovers()
    {
        var runtime = PreparedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(Manifest() with { S8EvidenceHead = "not-a-sha" }));
        Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(Manifest() with { SoftwareHead = "not-a-sha" }));
        Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(Manifest() with { SafetyApprover = "operator-a" }));
    }

    [Fact]
    public void Session_RejectsPlanAssetOutsideApprovedProfile()
    {
        var runtime = Runtime();
        runtime.DefineHardwareProfile(Profile());
        runtime.DefinePlan(Plan() with
        {
            PlanId = "PLAN-BAD-ASSET",
            Steps =
            [
                new HilTrialStepDefinition
                {
                    StepId = "bad-asset",
                    Kind = HilStepKind.PlcRead,
                    AssetId = "PLC-NOT-APPROVED",
                    ExpectedOutcome = "Must not be admitted.",
                    TimeoutSeconds = 30
                }
            ]
        });
        Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(Manifest() with { PlanId = "PLAN-BAD-ASSET" }));
    }

    [Fact]
    public void RecoveryReference_RequiresRecoveredSession()
    {
        var runtime = PreparedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(Manifest("HIL-2") with { RecoveryFromSessionId = "HIL-MISSING" }));
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
    public void SelfHostedRunner_RequiresHilLabels()
    {
        var runtime = ArmedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation() with { RunnerLabels = ["self-hosted"] }));
    }

    [Fact]
    public void BenchMismatch_CannotStartExecution()
    {
        var runtime = ArmedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation() with { BenchId = "BENCH-OTHER" }));
    }

    [Fact]
    public void ExecutionAttestation_RequiresMatchingSoftwareHeadAndIsolation()
    {
        var runtime = ArmedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation() with { SoftwareHead = "2222222222222222222222222222222222222222" }));
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation() with { ProductionNetworkIsolated = false }));
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation() with { UsesProductionCredentials = true }));
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
    public void StepEvidence_RequiresMatchingAssetAndDigest()
    {
        var runtime = RunningRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence(
            "HIL-1", Evidence(1, "read-plc", HilStepResult.Passed) with { AssetId = "RGV-HIL-1" }));
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence(
            "HIL-1", Evidence(1, "read-plc", HilStepResult.Passed) with { EvidenceSha256 = "bad" }));
    }

    [Fact]
    public void EvidenceSequence_MustBeStrictlyIncreasing()
    {
        var runtime = RunningRuntime();
        runtime.RecordEvidence("HIL-1", Evidence(2, "read-plc", HilStepResult.Passed));
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence("HIL-1", Evidence(1, "move-rgv", HilStepResult.Passed)));
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence("HIL-1", Evidence(2, "move-rgv", HilStepResult.Passed)));
    }

    [Fact]
    public void EvidenceOutsideSessionDuration_IsRejected()
    {
        var options = Options();
        options.MaximumSessionDurationMinutes = 30;
        var runtime = RunningRuntime(options);
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence(
            "HIL-1", Evidence(1, "read-plc", HilStepResult.Passed) with { OccurredAtUtc = Start.AddHours(1) }));
    }

    [Fact]
    public void Acceptance_RequiresProtocolMechanicalSiteAndEvidenceDigests()
    {
        var runtime = CompletedRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.Accept("HIL-1", Acceptance() with { SiteAccepted = false }));
        Assert.Throws<InvalidOperationException>(() => runtime.Accept("HIL-1", Acceptance() with { ProtocolEvidenceSha256 = "bad" }));

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
        var runtime = RunningRuntime(options);
        runtime.RecordEvidence("HIL-1", Evidence(1, "read-plc", HilStepResult.Passed));
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence("HIL-1", Evidence(2, "move-rgv", HilStepResult.Passed)));
    }

    [Fact]
    public void Abort_StopsSessionAndPreventsCompletion()
    {
        var runtime = RunningRuntime();
        var aborted = runtime.Abort("HIL-1", AbortRequest());
        Assert.Equal(HilSessionState.Aborted, aborted.State);
        Assert.True(aborted.RealHilExecuted);
        Assert.Throws<InvalidOperationException>(() => runtime.CompleteExecution("HIL-1"));
        Assert.Throws<InvalidOperationException>(() => runtime.RecordEvidence("HIL-1", Evidence(1, "read-plc", HilStepResult.Passed)));
    }

    [Fact]
    public void Recovery_AfterRealHilRequiresPhysicalSafeStateAndDualApproval()
    {
        var runtime = RunningRuntime();
        runtime.Abort("HIL-1", AbortRequest());
        Assert.Throws<InvalidOperationException>(() => runtime.Recover("HIL-1", Recovery() with { RealHardwareObserved = false }));
        Assert.Throws<InvalidOperationException>(() => runtime.Recover("HIL-1", Recovery() with { SafetyApprover = "operator-a" }));

        var recovered = runtime.Recover("HIL-1", Recovery());
        Assert.Equal(HilSessionState.Recovered, recovered.State);
        Assert.True(recovered.RecoveryVerified);
    }

    [Fact]
    public void RecoveredSession_CannotResumeAndReplacementMustReferenceRecovery()
    {
        var runtime = RecoveredRuntime();
        Assert.Throws<InvalidOperationException>(() => runtime.BeginExecution("HIL-1", Attestation()));

        var replacement = runtime.CreateSession(Manifest("HIL-2") with
        {
            RecoveryFromSessionId = "HIL-1",
            CreatedAtUtc = Start.AddHours(2)
        });
        Assert.Equal(HilSessionState.Defined, replacement.State);
        Assert.False(replacement.RealHilExecuted);
    }

    [Fact]
    public void EvidenceBundle_ReportsPassingStepsAndExternalDigest()
    {
        var runtime = CompletedRuntime();
        var bundle = runtime.GetEvidenceBundle("HIL-1");
        Assert.Equal(2, bundle.PlannedStepCount);
        Assert.Equal(2, bundle.PassingRealHardwareStepCount);
        Assert.True(bundle.RealHilExecuted);
        Assert.True(bundle.ReadyForAcceptance);
        Assert.Equal(Bundle, bundle.ExternalBundleSha256);
        Assert.Equal(64, bundle.EvidenceHash.Length);
    }

    [Fact]
    public void Readiness_DistinguishesArmedRealHilAndAccepted()
    {
        var armed = ArmedRuntime();
        var armedReadiness = armed.GetReadiness("HIL-1");
        Assert.True(armedReadiness.ReadyForRealHilExecution);
        Assert.False(armedReadiness.RealHilEvidencePresent);
        Assert.False(armedReadiness.S9Accepted);

        var running = RunningRuntime();
        Assert.True(running.GetReadiness("HIL-1").RealHilEvidencePresent);
        Assert.False(running.GetReadiness("HIL-1").S9Accepted);

        var completed = CompletedRuntime();
        completed.Accept("HIL-1", Acceptance());
        var accepted = completed.GetReadiness("HIL-1");
        Assert.True(accepted.ProtocolValidated);
        Assert.True(accepted.MechanicalSafetyAccepted);
        Assert.True(accepted.SiteAccepted);
        Assert.True(accepted.S9Accepted);
    }

    private static HilVerificationRuntime RecoveredRuntime()
    {
        var runtime = RunningRuntime();
        runtime.Abort("HIL-1", AbortRequest());
        runtime.Recover("HIL-1", Recovery());
        return runtime;
    }

    private static HilVerificationRuntime CompletedRuntime(HilVerificationOptions? options = null)
    {
        var runtime = RunningRuntime(options);
        runtime.RecordEvidence("HIL-1", Evidence(1, "read-plc", HilStepResult.Passed));
        runtime.RecordEvidence("HIL-1", Evidence(2, "move-rgv", HilStepResult.Passed));
        runtime.CompleteExecution("HIL-1");
        return runtime;
    }

    private static HilVerificationRuntime RunningRuntime(HilVerificationOptions? options = null)
    {
        var runtime = ArmedRuntime(options);
        runtime.BeginExecution("HIL-1", Attestation());
        return runtime;
    }

    private static HilVerificationRuntime ArmedRuntime(HilVerificationOptions? options = null)
    {
        var runtime = PreparedRuntime(options);
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

    private static HilVerificationRuntime Runtime(HilVerificationOptions? options = null) => new(options ?? Options());

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
        RequireSelfHostedHilRunner = true,
        AllowedEnvironments = ["HIL", "TrialRun"]
    };

    private static HilHardwareProfileDefinition Profile() => new()
    {
        ProfileId = "BENCH-PROFILE-1",
        BenchId = "BENCH-1",
        PlcProtocol = "S7",
        TopologyRevision = "HIL-TOPOLOGY-R1",
        ControllerAssetIds = ["PLC-HIL-1"],
        VehicleAssetIds = ["RGV-HIL-1"],
        ProductionNetworkIsolated = true,
        UsesProductionCredentials = false,
        ApprovedBy = "hil-owner-c",
        ApprovedAtUtc = Start.AddHours(-1)
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

    private static HilSessionManifest Manifest(string sessionId = "HIL-1") => new()
    {
        SessionId = sessionId,
        S8EvidenceHead = S8Head,
        SoftwareHead = SoftwareHead,
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
        ProcedureRevision = "HIL-SAFE-R1",
        Operator = "operator-a",
        SafetyApprover = "safety-b",
        VerifiedAtUtc = Start.AddMinutes(5)
    };

    private static HilExecutionAttestation Attestation() => new()
    {
        RunnerKind = "SelfHostedHil",
        RunnerName = "hil-runner-01",
        RunnerLabels = ["self-hosted", "wcs-hil"],
        BenchId = "BENCH-1",
        SoftwareHead = SoftwareHead,
        RealHardwareConnected = true,
        ProductionNetworkIsolated = true,
        UsesProductionCredentials = false,
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
        EvidenceSha256 = EvidenceDigest,
        RealHardwareObserved = true
    };

    private static HilAbortRequest AbortRequest() => new()
    {
        Reason = "Bench motion anomaly detected; stop and recover before any retry.",
        AbortedBy = "operator-a",
        AbortedAtUtc = Start.AddMinutes(20)
    };

    private static HilRecoveryRequest Recovery() => new()
    {
        MotionStopped = true,
        PlcOutputsSafe = true,
        MechanicalInterlocksRestored = true,
        EmergencyStopStateVerified = true,
        OperatorAreaClear = true,
        RealHardwareObserved = true,
        VerifiedBy = "operator-a",
        SafetyApprover = "safety-b",
        VerifiedAtUtc = Start.AddMinutes(25),
        EvidenceBundleSha256 = Bundle
    };

    private static HilAcceptanceRequest Acceptance() => new()
    {
        ProtocolValidated = true,
        MechanicalSafetyAccepted = true,
        SiteAccepted = true,
        AcceptedBy = "site-acceptor-c",
        AcceptedAtUtc = Start.AddHours(1),
        ProtocolEvidenceSha256 = ProtocolDigest,
        MechanicalSafetyEvidenceSha256 = MechanicalDigest,
        SiteAcceptanceEvidenceSha256 = SiteDigest,
        EvidenceBundleSha256 = Bundle
    };
}
