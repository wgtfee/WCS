namespace Wcs.Simulator.HilVerification;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Evidence/state-machine runtime for S9 HIL. It never performs hardware or network I/O.
/// Real hardware execution must be attested by a separate site-owned/self-hosted HIL runner.
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
        if (!definition.ProductionNetworkIsolated)
            throw new InvalidOperationException("HIL bench must be isolated from the production network.");
        if (definition.UsesProductionCredentials)
            throw new InvalidOperationException("HIL bench must not use production credentials.");
        if (definition.ControllerAssetIds.Count == 0)
            throw new InvalidOperationException("At least one controller asset is required.");
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
        if (!GitShaRegex().IsMatch(manifest.S8EvidenceHead))
            throw new InvalidOperationException("S8EvidenceHead must be an exact 40-character Git SHA.");
        if (!_profiles.ContainsKey(manifest.HardwareProfileId))
            throw new InvalidOperationException("Unknown HIL hardware profile.");
        if (!_plans.ContainsKey(manifest.PlanId))
            throw new InvalidOperationException("Unknown HIL trial plan.");
        RequireText(manifest.ChangeTicket, nameof(manifest.ChangeTicket), 256);
        RequireText(manifest.MaintenanceWindowId, nameof(manifest.MaintenanceWindowId), 256);
        RequireText(manifest.Operator, nameof(manifest.Operator), 256);
        RequireText(manifest.SafetyApprover, nameof(manifest.SafetyApprover), 256);
        if (_options.RequireDualApproval && string.Equals(manifest.Operator, manifest.SafetyApprover, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HIL operator and safety approver must be different people.");
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
        if (!attestation.RealHardwareConnected)
            throw new InvalidOperationException("S9 execution cannot start without real hardware connected.");
        if (!string.Equals(attestation.BenchId, profile.BenchId, StringComparison.Ordinal))
            throw new InvalidOperationException("HIL execution bench does not match the approved hardware profile.");
        RequireText(attestation.RunnerName, nameof(attestation.RunnerName), 256);
        if (!Sha256Regex().IsMatch(attestation.EvidenceBundleSha256))
            throw new InvalidOperationException("HIL execution evidence bundle SHA-256 is invalid.");
        session.Execution = attestation;
        session.State = HilSessionState.Running;
        session.Detail = "Real HIL execution attested by external runner; awaiting step evidence.";
        return Snapshot(session);
    }

    public HilSessionSnapshot RecordEvidence(string sessionId, HilEvidenceRecord evidence)
    {
        var session = RequiredSession(sessionId);
        ArgumentNullException.ThrowIfNull(evidence);
        RequireState(session, HilSessionState.Running);
        if (session.Evidence.Count >= _options.MaximumEvidenceRecordsPerSession)
            throw new InvalidOperationException("HIL evidence capacity reached for this session.");
        if (evidence.Sequence < 0 || session.Evidence.Any(item => item.Sequence == evidence.Sequence))
            throw new InvalidOperationException("HIL evidence sequence must be non-negative and unique.");
        if (evidence.Value.Length > _options.MaximumEvidenceValueCharacters)
            throw new InvalidOperationException("HIL evidence value exceeds MaximumEvidenceValueCharacters.");
        if (evidence.Kind == HilEvidenceKind.StepResult)
        {
            if (!_plans[session.Manifest.PlanId].Steps.Any(step => string.Equals(step.StepId, evidence.StepId, StringComparison.Ordinal)))
                throw new InvalidOperationException("HIL evidence references an unknown plan step.");
            if (!evidence.RealHardwareObserved)
                throw new InvalidOperationException("HIL step result must be observed from real hardware.");
        }
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
        session.Detail = "All HIL plan steps completed with passing real-hardware evidence; site acceptance still required.";
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
        if (!Sha256Regex().IsMatch(acceptance.EvidenceBundleSha256))
            throw new InvalidOperationException("Final HIL evidence bundle SHA-256 is invalid.");
        session.Acceptance = acceptance;
        session.State = HilSessionState.Accepted;
        session.Detail = "S9 HIL session accepted using explicit external/site evidence.";
        return Snapshot(session);
    }

    public HilSessionSnapshot Abort(string sessionId, string reason)
    {
        var session = RequiredSession(sessionId);
        if (session.State is HilSessionState.Completed or HilSessionState.Accepted or HilSessionState.Aborted or HilSessionState.Rejected)
            throw new InvalidOperationException("Terminal HIL sessions cannot be aborted again.");
        RequireText(reason, nameof(reason), 1_024);
        session.State = HilSessionState.Aborted;
        session.Detail = reason;
        return Snapshot(session);
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

    private HilSessionSnapshot Snapshot(SessionState session)
    {
        var realHilExecuted = session.Execution?.RealHardwareConnected == true &&
                              string.Equals(session.Execution.RunnerKind, "SelfHostedHil", StringComparison.Ordinal);
        return new HilSessionSnapshot(
            session.Manifest.SessionId,
            session.State,
            session.Manifest.HardwareProfileId,
            session.Manifest.PlanId,
            session.Manifest.S8EvidenceHead,
            realHilExecuted,
            session.Acceptance?.ProtocolValidated == true,
            session.Acceptance?.MechanicalSafetyAccepted == true,
            session.Acceptance?.SiteAccepted == true,
            session.Evidence.Count,
            ComputeEvidenceHash(session),
            session.Detail);
    }

    private string ComputeEvidenceHash(SessionState session)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            manifest = session.Manifest,
            profile = _profiles[session.Manifest.HardwareProfileId],
            plan = _plans[session.Manifest.PlanId],
            preflight = session.Preflight,
            execution = session.Execution,
            evidence = session.Evidence.OrderBy(item => item.Sequence).ToArray(),
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

    private sealed class SessionState(HilSessionManifest manifest)
    {
        public HilSessionManifest Manifest { get; } = manifest;
        public HilSessionState State { get; set; } = HilSessionState.Defined;
        public HilSafetyPreflight? Preflight { get; set; }
        public HilExecutionAttestation? Execution { get; set; }
        public List<HilEvidenceRecord> Evidence { get; } = [];
        public HilAcceptanceRequest? Acceptance { get; set; }
        public string? Detail { get; set; }
    }
}
