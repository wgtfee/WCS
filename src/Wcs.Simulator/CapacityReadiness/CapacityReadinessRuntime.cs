namespace Wcs.Simulator.CapacityReadiness;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;
using Wcs.Simulator.VirtualHealth;
using Wcs.Simulator.VirtualIntegration;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

/// <summary>
/// Repository-only S8 capacity harness. It composes the existing S7 runtime on the shared
/// SimulationStateStore and performs admission before provisioning any S2-S7 virtual resource.
/// </summary>
public sealed partial class CapacityReadinessRuntime
{
    private const string ProfileCountKey = "__s8.profile.count";
    private readonly SimulationStateStore _state;
    private readonly SimulationScenarioEngineOptions _engineOptions;
    private readonly CapacityReadinessOptions _options;
    private readonly VirtualIntegrationOptions _integration;
    private readonly VirtualPlcOptions _plc;
    private readonly VirtualRgvOptions _rgv;
    private readonly VirtualTrafficOptions _traffic;
    private readonly VirtualExternalOptions _external;
    private readonly VirtualHealthOptions _health;

    public CapacityReadinessRuntime(
        SimulationStateStore state,
        SimulationScenarioEngineOptions engineOptions,
        CapacityReadinessOptions options,
        VirtualIntegrationOptions integration,
        VirtualPlcOptions plc,
        VirtualRgvOptions rgv,
        VirtualTrafficOptions traffic,
        VirtualExternalOptions external,
        VirtualHealthOptions health)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _engineOptions = engineOptions ?? throw new ArgumentNullException(nameof(engineOptions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _integration = integration ?? throw new ArgumentNullException(nameof(integration));
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
        _rgv = rgv ?? throw new ArgumentNullException(nameof(rgv));
        _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));
        _external = external ?? throw new ArgumentNullException(nameof(external));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _engineOptions.Validate(); _options.Validate(); _integration.Validate(); _plc.Validate(); _rgv.Validate();
        _traffic.Validate(); _external.Validate(); _health.Validate();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,113}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdRegex();

    public CapacityAdmissionResult Preflight(CapacityProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var violations = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.ProfileId) || !ProfileIdRegex().IsMatch(profile.ProfileId)) violations.Add("ProfileId must match the S8 identifier contract and be at most 114 characters.");
        if (profile.MissionCount is < 1 || profile.MissionCount > _options.MaximumMissionsPerProfile) violations.Add("MissionCount exceeds S8 profile limit.");
        if (profile.ConcurrentMissions is < 1 || profile.ConcurrentMissions > _options.MaximumConcurrentMissions || profile.ConcurrentMissions > profile.MissionCount) violations.Add("ConcurrentMissions exceeds S8 profile limit.");
        if (profile.SegmentsPerMission is < 1 || profile.SegmentsPerMission > _options.MaximumSegmentsPerMission) violations.Add("SegmentsPerMission exceeds S8 profile limit.");
        if (profile.VirtualDurationMilliseconds < 1) violations.Add("VirtualDurationMilliseconds must be positive.");
        if (profile.Kind == CapacityProfileKind.EightHourVirtualSoak && profile.VirtualDurationMilliseconds != _options.EightHourVirtualDurationMilliseconds) violations.Add("EightHourVirtualSoak must be exactly 8 virtual hours.");
        if (profile.Kind == CapacityProfileKind.TwentyFourHourVirtualSoak && profile.VirtualDurationMilliseconds != _options.TwentyFourHourVirtualDurationMilliseconds) violations.Add("TwentyFourHourVirtualSoak must be exactly 24 virtual hours.");
        if (_state.Contains(ProfileKey(profile.ProfileId))) violations.Add("ProfileId is already registered in this S8 state store.");

        var missions = Math.Max(0, profile.MissionCount);
        var segmentsPerMission = Math.Max(0, profile.SegmentsPerMission);
        var concurrent = Math.Max(1, profile.ConcurrentMissions);
        var segments = checked((long)missions * segmentsPerMission);
        var estimatedStateEntries = checked(64L + missions * 28L + segments * 12L);
        var batchCount = missions == 0 ? 0 : (missions + concurrent - 1L) / concurrent;
        var minimumVirtualDuration = checked(batchCount * (segmentsPerMission * 1_000L + 101L));
        if (profile.VirtualDurationMilliseconds > 0 && profile.VirtualDurationMilliseconds < minimumVirtualDuration) violations.Add("VirtualDurationMilliseconds is too small for deterministic bounded batches.");
        if (segmentsPerMission * 1_000L + 100L >= _integration.ReservationLeaseMilliseconds) violations.Add("Mission duration would reach or exceed the configured S7 reservation lease.");
        if (missions > _integration.MaximumMissions) violations.Add("MissionCount exceeds SimulationVirtualIntegration.MaximumMissions.");
        if (profile.SegmentsPerMission > _integration.MaximumSegmentsPerMission) violations.Add("SegmentsPerMission exceeds SimulationVirtualIntegration.MaximumSegmentsPerMission.");
        if (missions > _plc.MaximumBlocks) violations.Add("MissionCount exceeds SimulationVirtualPlc.MaximumBlocks.");
        if (missions > _rgv.MaximumVehicles) violations.Add("MissionCount exceeds SimulationVirtualRgv.MaximumVehicles.");
        if (segments > _rgv.MaximumSegments) violations.Add("Aggregate segments exceed SimulationVirtualRgv.MaximumSegments.");
        if (profile.SegmentsPerMission > _rgv.MaximumRouteSegments) violations.Add("SegmentsPerMission exceeds SimulationVirtualRgv.MaximumRouteSegments.");
        if (segments > _traffic.MaximumZones) violations.Add("Aggregate segments exceed SimulationVirtualTraffic.MaximumZones.");
        if (segments > _traffic.MaximumReservations) violations.Add("Aggregate reservations exceed SimulationVirtualTraffic.MaximumReservations.");
        if (profile.SegmentsPerMission > _traffic.MaximumRollingLookAheadSegments) violations.Add("SegmentsPerMission exceeds SimulationVirtualTraffic.MaximumRollingLookAheadSegments.");
        if (missions > _external.MaximumEndpoints) violations.Add("MissionCount exceeds SimulationVirtualExternal.MaximumEndpoints.");
        if (missions > _external.MaximumRequests) violations.Add("MissionCount exceeds SimulationVirtualExternal.MaximumRequests.");
        if (missions > _health.MaximumAssets) violations.Add("MissionCount exceeds SimulationVirtualHealth.MaximumAssets.");
        if (estimatedStateEntries > _engineOptions.MaximumStateEntries) violations.Add("Estimated state entries exceed SimulationScenarioEngine.MaximumStateEntries.");
        if (ReadLong(ProfileCountKey) >= _options.MaximumProfiles) violations.Add("S8 profile registry has reached MaximumProfiles.");
        return new CapacityAdmissionResult(violations.Count == 0, violations, estimatedStateEntries);
    }

    public CapacityRunReport Run(CapacityProfileDefinition profile, DateTimeOffset startUtc)
    {
        var admission = Preflight(profile);
        if (!admission.Accepted)
            throw new InvalidOperationException("S8 capacity preflight rejected before provisioning: " + string.Join(" | ", admission.Violations));

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<CapacitySample>(Math.Min(profile.MissionCount, _options.MaximumSamplesPerProfile));
        Set(ProfileKey(profile.ProfileId), profile);
        var profileOrdinal = _state.Increment(ProfileCountKey, 1);
        var runtime = IntegrationRuntime();
        long sequence = 0;
        long lastCompletedOffset = 0;
        var peakConcurrent = 0;

        for (var batchStart = 0; batchStart < profile.MissionCount; batchStart += profile.ConcurrentMissions)
        {
            var batchSize = Math.Min(profile.ConcurrentMissions, profile.MissionCount - batchStart);
            var proportionalOffset = checked((profile.VirtualDurationMilliseconds * batchStart) / Math.Max(1, profile.MissionCount));
            var batchOffset = Math.Max(proportionalOffset, lastCompletedOffset);
            var batch = new List<string>(batchSize);

            for (var local = 0; local < batchSize; local++)
            {
                var missionIndex = batchStart + local;
                var prefix = $"P{profileOrdinal:D4}_{missionIndex:D5}";
                var missionId = $"{profile.ProfileId}-{prefix}";
                runtime.DefineMission(BuildMission(missionId, prefix, profileOrdinal, missionIndex, profile.SegmentsPerMission),
                    batchOffset, startUtc.AddMilliseconds(batchOffset));
                runtime.DispatchMission(missionId, batchOffset + 1, startUtc.AddMilliseconds(batchOffset + 1));
                batch.Add(missionId);
            }

            var active = runtime.ListMissions().Count(x => x.State is VirtualIntegrationMissionState.Dispatched or VirtualIntegrationMissionState.Moving or VirtualIntegrationMissionState.Completed);
            peakConcurrent = Math.Max(peakConcurrent, active);
            var operationOffset = batchOffset + 1;
            for (var segment = 0; segment < profile.SegmentsPerMission; segment++)
            {
                operationOffset += 1_000;
                foreach (var missionId in batch)
                    runtime.AdvanceMission(missionId, operationOffset, startUtc.AddMilliseconds(operationOffset));
            }

            operationOffset += 100;
            foreach (var missionId in batch)
            {
                runtime.AcknowledgeMission(missionId, operationOffset, startUtc.AddMilliseconds(operationOffset));
                if (samples.Count < _options.MaximumSamplesPerProfile)
                {
                    samples.Add(new CapacitySample(++sequence, operationOffset, runtime.ListMissions().Count,
                        runtime.ListMissions().Count(x => x.State == VirtualIntegrationMissionState.Acknowledged),
                        _state.Count, _state.ComputeHash()));
                }
            }
            lastCompletedOffset = operationOffset + 1;
        }

        var missions = runtime.ListMissions().Where(x => x.MissionId.StartsWith(profile.ProfileId + "-", StringComparison.Ordinal)).ToArray();
        var conservation = missions.Length == profile.MissionCount &&
            missions.All(x => x.State == VirtualIntegrationMissionState.Acknowledged) &&
            missions.All(x => runtime.GetConsistency(x.MissionId, profile.VirtualDurationMilliseconds).IsConsistent);
        var bounded = _state.Count <= _engineOptions.MaximumStateEntries && samples.Count <= _options.MaximumSamplesPerProfile && peakConcurrent <= profile.ConcurrentMissions;
        stopwatch.Stop();
        process.Refresh();
        var rssAfter = process.WorkingSet64;
        var rssGrowth = Math.Max(0, rssAfter - rssBefore);
        var resourceBudget = stopwatch.ElapsedMilliseconds <= _options.MaximumWallClockMilliseconds && rssGrowth <= _options.MaximumRssGrowthBytes;
        var workloadStateHash = _state.ComputeHash();
        var evidenceHash = ComputeEvidenceHash(profile, peakConcurrent, samples, workloadStateHash);
        var snapshot = new CapacityProfileSnapshot(profile.ProfileId, profile.Kind, CapacityProfileState.Completed,
            profile.MissionCount, profile.ConcurrentMissions, profile.SegmentsPerMission, profile.VirtualDurationMilliseconds,
            peakConcurrent, samples.Count, conservation, bounded, workloadStateHash, evidenceHash,
            $"acknowledged={missions.Count(x => x.State == VirtualIntegrationMissionState.Acknowledged)};peakConcurrent={peakConcurrent};stateEntries={_state.Count}");
        Set(ProfileResultKey(profile.ProfileId), snapshot);
        return new CapacityRunReport(snapshot, admission, samples.ToArray(), stopwatch.ElapsedMilliseconds,
            rssBefore, rssAfter, rssGrowth, GC.CollectionCount(0)-gen0, GC.CollectionCount(1)-gen1, GC.CollectionCount(2)-gen2,
            process.Threads.Count, TryGetHandleCount(process), resourceBudget);
    }

    public HilReadinessSnapshot BuildHilReadiness(
        CapacityRunReport eightHour,
        CapacityRunReport twentyFourHour,
        bool deterministicReplayVerified,
        bool checkpointRestoreVerified,
        bool simulationIsolationVerified,
        bool productionFailClosedVerified,
        bool noProductionControlWritesVerified)
    {
        var capacityBoundary = eightHour.Admission.Accepted && twentyFourHour.Admission.Accepted;
        var conservation = eightHour.Profile.ConservationSatisfied && twentyFourHour.Profile.ConservationSatisfied;
        var eight = eightHour.Profile.Kind == CapacityProfileKind.EightHourVirtualSoak && eightHour.Profile.State == CapacityProfileState.Completed && eightHour.ResourceBudgetSatisfied;
        var twentyFour = twentyFourHour.Profile.Kind == CapacityProfileKind.TwentyFourHourVirtualSoak && twentyFourHour.Profile.State == CapacityProfileState.Completed && twentyFourHour.ResourceBudgetSatisfied;
        var softwareReady = simulationIsolationVerified && productionFailClosedVerified && deterministicReplayVerified && checkpointRestoreVerified &&
            capacityBoundary && eight && twentyFour && conservation && noProductionControlWritesVerified;
        return new HilReadinessSnapshot(simulationIsolationVerified, productionFailClosedVerified, deterministicReplayVerified,
            checkpointRestoreVerified, capacityBoundary, eight, twentyFour, conservation, noProductionControlWritesVerified,
            false, false, false, softwareReady,
            ["Real PLC/RGV/MES/industrial network HIL execution", "Mechanical safety/interlock acceptance", "Site topology, credentials and trial-run acceptance"]);
    }

    public CapacityProfileSnapshot? TryGetProfileResult(string profileId)
    {
        if (!_state.TryGet(ProfileResultKey(profileId), out var value)) return null;
        return value.Deserialize<CapacityProfileSnapshot>();
    }

    private VirtualIntegrationRuntime IntegrationRuntime() => new(_state, _integration, _plc, _rgv, _traffic, _external, _health);

    private static VirtualIntegrationMissionDefinition BuildMission(string missionId, string prefix, long profileOrdinal, int missionIndex, int segmentCount)
    {
        var segments = new List<VirtualIntegrationSegmentDefinition>(segmentCount);
        for (var i = 0; i < segmentCount; i++)
            segments.Add(new VirtualIntegrationSegmentDefinition($"S-{prefix}-{i:D3}", $"N-{prefix}-{i:D3}", $"N-{prefix}-{i+1:D3}", 1_000, 1_000));
        return new VirtualIntegrationMissionDefinition
        {
            MissionId = missionId,
            PlcBlockKey = $"PLC8P{profileOrdinal:D4}.DB{missionIndex}",
            VehicleId = $"RGV-{prefix}", LoadId = $"LOAD-{prefix}",
            SourceNodeId = segments[0].FromNodeId, DestinationNodeId = segments[^1].ToNodeId,
            ExternalEndpointId = $"MES-{prefix}", ExternalSystemKind = VirtualExternalSystemKind.Mes,
            HealthAssetId = $"ASSET-{prefix}", Priority = 100, VehicleSpeedMillimetersPerSecond = 1_000,
            VehicleBatteryPercent = 100, InitialHealthScore = 95, InitialFusionRiskScore = 0.05, Segments = segments
        };
    }

    private static string ComputeEvidenceHash(CapacityProfileDefinition profile, int peakConcurrent, IReadOnlyList<CapacitySample> samples, string workloadStateHash)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            profile.ProfileId,
            Kind = profile.Kind.ToString(),
            profile.MissionCount,
            profile.ConcurrentMissions,
            profile.SegmentsPerMission,
            profile.VirtualDurationMilliseconds,
            PeakConcurrentMissions = peakConcurrent,
            Samples = samples.Select(x => new { x.Sequence, x.VirtualOffsetMilliseconds, x.DefinedMissions, x.AcknowledgedMissions, x.StateEntryCount, x.StateHash }).ToArray(),
            WorkloadStateHash = workloadStateHash
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private long ReadLong(string key) => _state.TryGet(key, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private void Set<T>(string key, T value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        _state.Set(key, doc.RootElement);
    }
    private static int TryGetHandleCount(Process process) { try { return process.HandleCount; } catch { return -1; } }
    private static string ProfileKey(string id) => $"__s8.profile.{id}";
    private static string ProfileResultKey(string id) => $"__s8.result.{id}";
}
