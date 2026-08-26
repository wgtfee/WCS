namespace Wcs.Simulator.HilVerification;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Evidence/state-machine runtime for S9 HIL. It never performs hardware or network I/O.
/// Real hardware execution must be attested by a separate site-owned/self-hosted HIL runner.
/// Aborted sessions can only be recovered to a terminal safe state; they can never resume Running.
/// </summary>
public sealed partial class HilVerificationRuntime
{
    private readonly HilVerificationOptions _options;
    private readonly Dictionary<string, HilHardwareProfileDefinition> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HilTrialPlanDefinition> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public HilVerificationRuntime(HilVerificationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitShaRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    public HilHardwareProfileDefinition DefineHardwareProfile(HilHardwareProfileDefinition definition)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(definition);
        if (_profiles.Count >= _options.MaximumHardwareProfiles)
            throw new InvalidOperationException("HIL hardware profile capacity reached.");

        var profileId = NormalizeId(definition.ProfileId, nameof(definition.ProfileId));
        NormalizeId(definition.BenchId, nameof(definition.BenchId));
        RequireText(definition.PlcProtocol, nameof(definition.PlcProtocol), 64);
        RequireText(definition.TopologyRevision, nameof(definition.TopologyRevision), 128);
        RequireText(definition.ApprovedBy, nameof(definition.ApprovedBy), 256);
        if (definition.ApprovedAtUtc == default)
            throw new InvalidOperationException("HIL hardware profile approval timestamp is required.");
        if (!definition.ProductionNetworkIsolated)
            throw new InvalidOperationException("HIL bench must be isolated from the production network.");
        if (definition.UsesProductionCredentials)
            throw new InvalidOperationException("HIL bench must not use production credentials.");
        if (definition.ControllerAssetIds.Count == 0)
            throw new InvalidOperationException("At least one controller asset is required.");
        if (definition.ControllerAssetIds.Count > 1_000 || definition.VehicleAssetIds.Count > 1_000)
            throw new InvalidOperationException("HIL hardware profile asset lists are bounded to 1,000 items each.");
        ValidateIds(definition.ControllerAssetIds, "ControllerAssetId");
        ValidateIds(definition.VehicleAssetIds, "VehicleAssetId");
        if (definition.ControllerAssetIds.Concat(definition.VehicleAssetIds).Distinct(StringComparer.Ordinal).Count()
            != definition.ControllerAssetIds.Count + definition.VehicleAssetIds.Count)
            throw new InvalidOperationException("HIL asset ids must be unique within a hardware profile.");
        if (_profiles.ContainsKey(profileId))
            throw new InvalidOperationException($"HIL hardware profile '{profileId}' already exists.");

        _profiles.Add(profileId, definition with { ProfileId = profileId });
        return _profiles[profileId];
    }

    public HilTrialPlanDefinition DefinePlan(HilTrialPlanDefinition definition)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(definition);
        if (_plans.Count >= _options.MaximumPlans)
            throw new InvalidOperationException("HIL plan capacity reached.");
        var planId = NormalizeId(definition.PlanId, nameof(definition.PlanId));
        RequireText(definition.Version, nameof(definition.Version), 64);
        if (definition.Steps.Count is < 1 || definition.Steps.Count > _options.MaximumStepsPerPlan)
            throw new InvalidOperationException("HIL plan step count is outside MaximumStepsPerPlan.");
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            var stepId = NormalizeId(step.StepId, nameof(step.StepId));
            if (!stepIds.Add(stepId)) throw new InvalidOperationException($"Duplicate HIL step '{stepId}'.");
            NormalizeId(step.AssetId, nameof(step.AssetId));
            RequireText(step.ExpectedOutcome, nameof(step.ExpectedOutcome), 1_024);
            if (step.TimeoutSeconds is < 1 or > 86_400)
                throw new InvalidOperationException("HIL step timeout must be between 1 second and 24 hours.");
            if (step.Kind == HilStepKind.VehicleMove && !step.RequiresMotion)
                throw new InvalidOperationException("VehicleMove steps must explicitly require motion.");
            if (step.Kind == HilStepKind.ControlledPlcWrite && !step.RequiresControlWrite)
                throw new InvalidOperationException("ControlledPlcWrite steps must explicitly require a control write.");
        }
        if (_plans.ContainsKey(planId))
            throw new InvalidOperationException($"HIL plan '{planId}' already exists.");
        _plans.Add(planId, definition with { PlanId = planId });
        return _plans[planId];
    }

    public HilSessionSnapshot CreateSession(HilSessionManifest manifest)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(manifest);
        if (_sessions.Count >= _options.MaximumSessions)
            throw new InvalidOperationException("HIL session capacity reached.");
        var sessionId = NormalizeId(manifest.SessionId, nameof(manifest.SessionId));
        RequireGitSha(manifest.S8EvidenceHead, nameof(manifest.S8EvidenceHead));
        RequireGitSha(manifest.SoftwareHead, nameof(manifest.SoftwareHead));
        if (!_profiles.TryGetValue(manifest.HardwareProfileId, out var profile))
            throw new InvalidOperationException("Unknown HIL hardware profile.");
        if (!_plans.TryGetValue(manifest.PlanId, out var plan))
            throw new InvalidOperationException("Unknown HIL trial plan.");
        RequireText(manifest.ChangeTicket, nameof(manifest.ChangeTicket), 256);
        RequireText(manifest.MaintenanceWindowId, nameof(manifest.MaintenanceWindowId), 256);
        RequireText(manifest.Operator, nameof(manifest.Operator), 256);
        RequireText(manifest.SafetyApprover, nameof(manifest.SafetyApprover), 256);
        if (manifest.CreatedAtUtc == default)
            throw new InvalidOperationException("HIL session creation timestamp is required.");
        EnsureDistinctApprovers(manifest.Operator, manifest.SafetyApprover);

        var profileAssets = profile.ControllerAssetIds.Concat(profile.VehicleAssetIds).ToHashSet(StringComparer.Ordinal);
        var unknownStep = plan.Steps.FirstOrDefault(step => !profileAssets.Contains(step.AssetId));
        if (unknownStep is not null)
            throw new InvalidOperationException($"HIL plan step '{unknownStep.StepId}' references asset '{unknownStep.AssetId}' outside the approved hardware profile.");

        if (!string.IsNullOrWhiteSpace(manifest.RecoveryFromSessionId))
        {
            var recoveredId = NormalizeId(manifest.RecoveryFromSessionId, nameof(manifest.RecoveryFromSessionId));
            if (string.Equals(recoveredId, sessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("A HIL session cannot recover from itself.");
            if (!_sessions.TryGetValue(recoveredId, out var recoveredSession) || recoveredSession.State != HilSessionState.Recovered)
                throw new InvalidOperationException("RecoveryFromSessionId must reference an existing Recovered HIL session.");
        }

        if (_sessions.ContainsKey(sessionId))
            throw new InvalidOperationException($"HIL session '{sessionId}' already exists.");

        _sessions.Add(sessionId, new SessionState(manifest with { SessionId = sessionId }));
        return Snapshot(_sessions[sessionId]);
    }

    public HilSessionSnapshot SubmitPreflight(string sessionId, HilSafetyPreflight preflight)
    {
        var session = RequiredSession(sessionId);
        ArgumentNullException.ThrowIfNull(preflight);
        RequireState(session, HilSessionState.Defined);
        RequireText(preflight.ProcedureRevision, nameof(preflight.ProcedureRevision), 128);
        if (preflight.VerifiedAtUtc < session.Manifest.CreatedAtUtc)
            throw new InvalidOperationException("HIL preflight cannot precede session creation.");

        var passed = preflight.EmergencyStopVerified && preflight.MechanicalInterlocksVerified &&
                     preflight.GuardingVerified && preflight.NetworkIsolationVerified &&
                     preflight.MaintenanceModeVerified && preflight.OperatorAreaClear;
        if (_options.RequireDualApproval && string.Equals(preflight.Operator, preflight.SafetyApprover, StringComparison.OrdinalIgnoreCase))
            passed = false;
        if (!string.Equals(preflight.Operator, session.Manifest.Operator, StringComparison.Ordinal) ||
            !string.Equals(preflight.SafetyApprover, session.Manifest.SafetyApprover, StringComparison.Ordinal))
            passed = false;

        session.Preflight = preflight;
        session.State = passed ? HilSessionState.PreflightPassed : HilSessionState.Rejected;
        session.Detail = passed ? "Safety preflight passed." : "Safety preflight rejected.";
        return Snapshot(session);
    }

    public HilSessionSnapshot Arm(string sessionId)
    {
        var session = RequiredSession(sessionId);
        RequireState(session, HilSessionState.PreflightPassed);
        session.State = HilSessionState.Armed;
        session.Detail = "HIL session armed; no hardware command has been issued by this runtime.";
        return Snapshot(session);
    }

    public HilSessionSnapshot BeginExecution(string sessionId, HilExecutionAttestation attestation)
    {
        var session = RequiredSession(sessionId);
        ArgumentNullException.ThrowIfNull(attestation);
        RequireState(session, HilSessionState.Armed);
        var profile = _profiles[session.Manifest.HardwareProfileId];
        if (_options.RequireSelfHostedHilRunner && !string.Equals(attestation.RunnerKind, "SelfHostedHil", StringComparison.Ordinal))
            throw new InvalidOperationException("S9 real HIL execution requires a SelfHostedHil runner attestation.");
        if (_options.RequireSelfHostedHilRunner &&
            (!attestation.RunnerLabels.Contains("self-hosted", StringComparer.OrdinalIgnoreCase) ||
             !attestation.RunnerLabels.Contains("wcs-hil", StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("S9 real HIL execution requires self-hosted and wcs-hil runner labels.");
        if (!attestation.RealHardwareConnected)
            throw new InvalidOperationException("S9 execution cannot start without real hardware connected.");
        if (!attestation.ProductionNetworkIsolated)
            throw new InvalidOperationException("S9 execution requires production-network isolation attestation.");
        if (attestation.UsesProductionCredentials)
            throw new InvalidOperationException("S9 execution must not use production credentials.");
        if (!string.Equals(attestation.BenchId, profile.BenchId, StringComparison.Ordinal))
            throw new InvalidOperationException("HIL execution bench does not match the approved hardware profile.");
        RequireGitSha(attestation.SoftwareHead, nameof(attestation.SoftwareHead));
        if (!string.Equals(attestation.SoftwareHead, session.Manifest.SoftwareHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HIL execution software head does not match the session manifest.");
        RequireText(attestation.RunnerName, nameof(attestation.RunnerName), 256);
        RequireSha256(attestation.EvidenceBundleSha256, nameof(attestation.EvidenceBundleSha256));
        if (attestation.StartedAtUtc < session.Preflight!.VerifiedAtUtc)
            throw new InvalidOperationException("HIL execution cannot start before the approved safety preflight.");
        session.Execution = attestation;
        session.State = HilSessionState.Running;
        session.Detail = "Real HIL execution attested by external self-hosted runner; awaiting step evidence.";
        return Snapshot(session);
    }

    public HilSessionSnapshot RecordEvidence(string sessionId, HilEvidenceRecord evidence)
    {
        var session = RequiredSession(sessionId);
        ArgumentNullException.ThrowIfNull(evidence);
        RequireState(session, HilSessionState.Running);
        if (session.Evidence.Count >= _options.MaximumEvidenceRecordsPerSession)
            throw new InvalidOperationException("HIL evidence capacity reached for this session.");
        if (evidence.Sequence < 0 || (session.Evidence.Count > 0 && evidence.Sequence <= session.Evidence[^1].Sequence))
            throw new InvalidOperationException("HIL evidence sequence must be non-negative and strictly increasing.");
        if (evidence.Value.Length > _options.MaximumEvidenceValueCharacters)
            throw new InvalidOperationException("HIL evidence value exceeds MaximumEvidenceValueCharacters.");
        RequireSha256(evidence.EvidenceSha256, nameof(evidence.EvidenceSha256));
        if (evidence.OccurredAtUtc < session.Execution!.StartedAtUtc)
            throw new InvalidOperationException("HIL evidence cannot precede execution start.");
        if (evidence.OccurredAtUtc > session.Execution.StartedAtUtc.AddMinutes(_options.MaximumSessionDurationMinutes))
            throw new InvalidOperationException("HIL evidence exceeds MaximumSessionDurationMinutes.");

        if (evidence.Kind == HilEvidenceKind.StepResult)
        {
            var step = _plans[session.Manifest.PlanId].Steps
                .SingleOrDefault(item => string.Equals(item.StepId, evidence.StepId, StringComparison.Ordinal));
            if (step is null)
                throw new InvalidOperationException("HIL evidence references an unknown plan step.");
            if (!string.Equals(step.AssetId, evidence.AssetId, StringComparison.Ordinal))
                throw new InvalidOperationException("HIL step evidence asset does not match the approved trial-plan step.");
            if (!evidence.RealHardwareObserved)
                throw new InvalidOperationException("HIL step result must be observed from real hardware.");
        }
        if (evidence.Kind is HilEvidenceKind.Safety or HilEvidenceKind.Recovery && !evidence.RealHardwareObserved)
            throw new InvalidOperationException("HIL safety/recovery evidence must be observed from real hardware.");

        session.Evidence.Add(evidence);
        return Snapshot(session);
    }

    public HilSessionSnapshot CompleteExecution(string sessionId)
    {
        var session = RequiredSession(sessionId);
        RequireState(session, HilSessionState.Running);
        var plan = _plans[session.Manifest.PlanId];
        foreach (var step in plan.Steps)
        {
            var results = session.Evidence
                .Where(item => item.Kind == HilEvidenceKind.StepResult && string.Equals(item.StepId, step.StepId, StringComparison.Ordinal))
                .OrderBy(item => item.Sequence)
                .ToArray();
            if (results.Length == 0 || results.Any(item => item.Result == HilStepResult.Failed) ||
                results[^1].Result != HilStepResult.Passed || !results[^1].RealHardwareObserved)
                throw new InvalidOperationException($"HIL step '{step.StepId}' has not completed with a passing real-hardware result.");
        }
        session.State = HilSessionState.Completed;
        session.Detail = "All HIL plan steps completed with passing real-hardware evidence; protocol, mechanical-safety and site acceptance are still required.";
        return Snapshot(session);
    }

    public HilSessionSnapshot Accept(string sessionId, HilAcceptanceRequest acceptance)
    {
        var session = RequiredSession(sessionId);
        ArgumentNullException.ThrowIfNull(acceptance);
        RequireState(session, HilSessionState.Completed);
        if (!acceptance.ProtocolValidated || !acceptance.MechanicalSafetyAccepted || !acceptance.SiteAccepted)
            throw new InvalidOperationException("S9 acceptance requires explicit protocol, mechanical-safety and site acceptance.");
        RequireText(acceptance.AcceptedBy, nameof(acceptance.AcceptedBy), 256);
        RequireSha256(acceptance.ProtocolEvidenceSha256, nameof(acceptance.ProtocolEvidenceSha256));
        RequireSha256(acceptance.MechanicalSafetyEvidenceSha256, nameof(acceptance.MechanicalSafetyEvidenceSha256));
        RequireSha256(acceptance.SiteAcceptanceEvidenceSha256, nameof(acceptance.SiteAcceptanceEvidenceSha256));
        RequireSha256(acceptance.EvidenceBundleSha256, nameof(acceptance.EvidenceBundleSha256));
        if (acceptance.AcceptedAtUtc < session.Execution!.StartedAtUtc)
            throw new InvalidOperationException("S9 acceptance timestamp cannot precede real-HIL execution.");
        session.Acceptance = acceptance;
        session.State = HilSessionState.Accepted;
        session.Detail = "S9 HIL session accepted using explicit protocol, mechanical-safety and site evidence.";
        return Snapshot(session);
    }

    public HilSessionSnapshot Abort(string sessionId, HilAbortRequest abort)
    {
        var session = RequiredSession(sessionId);
        ArgumentNullException.ThrowIfNull(abort);
        if (session.State is HilSessionState.Completed or HilSessionState.Accepted or HilSessionState.Aborted or HilSessionState.Recovered or HilSessionState.Rejected)
            throw new InvalidOperationException("Terminal HIL sessions cannot be aborted again.");
        RequireText(abort.Reason, nameof(abort.Reason), 1_024);
        RequireText(abort.AbortedBy, nameof(abort.AbortedBy), 256);
        if (abort.AbortedAtUtc < session.Manifest.CreatedAtUtc)
            throw new InvalidOperationException("HIL abort timestamp cannot precede session creation.");
        session.Abort = abort;
        session.State = HilSessionState.Aborted;
        session.Detail = $"HIL session aborted: {abort.Reason}. Recovery must be verified before a replacement session can reference it.";
        return Snapshot(session);
    }

    public HilSessionSnapshot Recover(string sessionId, HilRecoveryRequest recovery)
    {
        var session = RequiredSession(sessionId);
        ArgumentNullException.ThrowIfNull(recovery);
        RequireState(session, HilSessionState.Aborted);
        var passed = recovery.MotionStopped && recovery.PlcOutputsSafe && recovery.MechanicalInterlocksRestored &&
                     recovery.EmergencyStopStateVerified && recovery.OperatorAreaClear;
        if (!passed)
            throw new InvalidOperationException("HIL recovery requires motion stopped, safe PLC outputs, restored interlocks, verified emergency-stop state and clear operator area.");
        RequireText(recovery.VerifiedBy, nameof(recovery.VerifiedBy), 256);
        RequireText(recovery.SafetyApprover, nameof(recovery.SafetyApprover), 256);
        EnsureDistinctApprovers(recovery.VerifiedBy, recovery.SafetyApprover);
        if (!string.Equals(recovery.VerifiedBy, session.Manifest.Operator, StringComparison.Ordinal) ||
            !string.Equals(recovery.SafetyApprover, session.Manifest.SafetyApprover, StringComparison.Ordinal))
            throw new InvalidOperationException("HIL recovery must be verified by the session operator and safety approver.");
        if (RealHilExecuted(session) && !recovery.RealHardwareObserved)
            throw new InvalidOperationException("Recovery after real-HIL execution must be observed from real hardware.");
        if (recovery.VerifiedAtUtc < session.Abort!.AbortedAtUtc)
            throw new InvalidOperationException("HIL recovery timestamp cannot precede abort.");
        RequireSha256(recovery.EvidenceBundleSha256, nameof(recovery.EvidenceBundleSha256));
        session.Recovery = recovery;
        session.State = HilSessionState.Recovered;
        session.Detail = "HIL session recovered to a verified safe terminal state. The same session cannot resume; create a replacement session referencing RecoveryFromSessionId.";
        return Snapshot(session);
    }

    public HilEvidenceBundleSnapshot GetEvidenceBundle(string sessionId)
    {
        var session = RequiredSession(sessionId);
        var plan = _plans[session.Manifest.PlanId];
        var passingSteps = plan.Steps.Count(step => session.Evidence.Any(item =>
            item.Kind == HilEvidenceKind.StepResult &&
            string.Equals(item.StepId, step.StepId, StringComparison.Ordinal) &&
            item.Result == HilStepResult.Passed &&
            item.RealHardwareObserved));
        var externalBundle = session.Acceptance?.EvidenceBundleSha256
                             ?? session.Recovery?.EvidenceBundleSha256
                             ?? session.Execution?.EvidenceBundleSha256
                             ?? string.Empty;
        return new HilEvidenceBundleSnapshot(
            session.Manifest.SessionId,
            session.State,
            session.Manifest.HardwareProfileId,
            session.Manifest.PlanId,
            session.Manifest.SoftwareHead,
            plan.Steps.Count,
            passingSteps,
            session.Evidence.Count,
            session.Evidence.Count == 0 ? -1 : session.Evidence[^1].Sequence,
            RealHilExecuted(session),
            session.Recovery is not null,
            session.State == HilSessionState.Completed && RealHilExecuted(session),
            ComputeEvidenceHash(session),
            externalBundle);
    }

    public HilSoftwareReadinessReport GetReadiness(string sessionId)
    {
        var session = RequiredSession(sessionId);
        return new HilSoftwareReadinessReport(
            GovernanceEnabled: _options.Enabled,
            ProductionFailClosed: !_options.EffectiveAllowedEnvironments.Contains("Production", StringComparer.OrdinalIgnoreCase),
            SelfHostedRunnerRequired: _options.RequireSelfHostedHilRunner,
            DualApprovalRequired: _options.RequireDualApproval,
            RecoveryFlowSupported: true,
            EvidenceIntegrityRequired: true,
            ReadyForRealHilExecution: session.State == HilSessionState.Armed,
            RealHilEvidencePresent: RealHilExecuted(session),
            ProtocolValidated: session.Acceptance?.ProtocolValidated == true,
            MechanicalSafetyAccepted: session.Acceptance?.MechanicalSafetyAccepted == true,
            SiteAccepted: session.Acceptance?.SiteAccepted == true,
            S9Accepted: session.State == HilSessionState.Accepted);
    }

    public HilSessionSnapshot GetSession(string sessionId) => Snapshot(RequiredSession(sessionId));

    public IReadOnlyList<HilSessionSnapshot> ListSessions() => _sessions.Values
        .OrderBy(item => item.Manifest.SessionId, StringComparer.Ordinal)
        .Select(Snapshot)
        .ToArray();

    private SessionState RequiredSession(string sessionId)
    {
        EnsureEnabled();
        var id = NormalizeId(sessionId, nameof(sessionId));
        return _sessions.TryGetValue(id, out var session)
            ? session
            : throw new KeyNotFoundException($"Unknown HIL session '{id}'.");
    }

    private HilSessionSnapshot Snapshot(SessionState session) => new(
        session.Manifest.SessionId,
        session.State,
        session.Manifest.HardwareProfileId,
        session.Manifest.PlanId,
        session.Manifest.S8EvidenceHead,
        session.Manifest.SoftwareHead,
        RealHilExecuted(session),
        session.Recovery is not null,
        session.Acceptance?.ProtocolValidated == true,
        session.Acceptance?.MechanicalSafetyAccepted == true,
        session.Acceptance?.SiteAccepted == true,
        session.Evidence.Count,
        ComputeEvidenceHash(session),
        session.Detail);

    private static bool RealHilExecuted(SessionState session) =>
        session.Execution?.RealHardwareConnected == true &&
        session.Execution.ProductionNetworkIsolated &&
        !session.Execution.UsesProductionCredentials &&
        string.Equals(session.Execution.RunnerKind, "SelfHostedHil", StringComparison.Ordinal) &&
        session.Execution.RunnerLabels.Contains("self-hosted", StringComparer.OrdinalIgnoreCase) &&
        session.Execution.RunnerLabels.Contains("wcs-hil", StringComparer.OrdinalIgnoreCase);

    private string ComputeEvidenceHash(SessionState session)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            manifest = session.Manifest,
            profile = _profiles[session.Manifest.HardwareProfileId],
            plan = _plans[session.Manifest.PlanId],
            preflight = session.Preflight,
            execution = session.Execution,
            evidence = session.Evidence.ToArray(),
            abort = session.Abort,
            recovery = session.Recovery,
            acceptance = session.Acceptance,
            state = session.State
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("HilVerification is disabled.");
    }

    private static void RequireState(SessionState session, HilSessionState expected)
    {
        if (session.State != expected)
            throw new InvalidOperationException($"HIL session must be in {expected} state, actual={session.State}.");
    }

    private void EnsureDistinctApprovers(string first, string second)
    {
        if (_options.RequireDualApproval && string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HIL operator/verifier and safety approver must be different people.");
    }

    private static string NormalizeId(string value, string name)
    {
        RequireText(value, name, 128);
        var trimmed = value.Trim();
        if (!IdRegex().IsMatch(trimmed)) throw new InvalidOperationException($"{name} contains unsupported characters.");
        return trimmed;
    }

    private static void ValidateIds(IEnumerable<string> values, string name)
    {
        foreach (var value in values) _ = NormalizeId(value, name);
    }

    private static void RequireText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
            throw new InvalidOperationException($"{name} is required and must be at most {maxLength} characters.");
    }

    private static void RequireGitSha(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !GitShaRegex().IsMatch(value))
            throw new InvalidOperationException($"{name} must be an exact 40-character Git SHA.");
    }

    private static void RequireSha256(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Sha256Regex().IsMatch(value))
            throw new InvalidOperationException($"{name} must be a 64-character SHA-256 digest.");
    }

    private sealed class SessionState(HilSessionManifest manifest)
    {
        public HilSessionManifest Manifest { get; } = manifest;
        public HilSessionState State { get; set; } = HilSessionState.Defined;
        public HilSafetyPreflight? Preflight { get; set; }
        public HilExecutionAttestation? Execution { get; set; }
        public List<HilEvidenceRecord> Evidence { get; } = [];
        public HilAbortRequest? Abort { get; set; }
        public HilRecoveryRequest? Recovery { get; set; }
        public HilAcceptanceRequest? Acceptance { get; set; }
        public string? Detail { get; set; }
    }
}
