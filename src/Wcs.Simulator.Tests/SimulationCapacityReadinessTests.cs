namespace Wcs.Simulator.Tests;

using Wcs.Simulator.CapacityReadiness;
using Wcs.Simulator.ScenarioEngine;
using Wcs.Simulator.VirtualExternal;
using Wcs.Simulator.VirtualHealth;
using Wcs.Simulator.VirtualIntegration;
using Wcs.Simulator.VirtualPlc;
using Wcs.Simulator.VirtualRgv;
using Wcs.Simulator.VirtualTraffic;

public sealed class SimulationCapacityReadinessTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Options_RejectAlteredVirtualSoakDurations()
    {
        Assert.Throws<InvalidOperationException>(() => new CapacityReadinessOptions { EightHourVirtualDurationMilliseconds = 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new CapacityReadinessOptions { TwentyFourHourVirtualDurationMilliseconds = 1 }.Validate());
    }

    [Fact]
    public void Preflight_RejectsAggregateCapacityBeforeProvisioningAnything()
    {
        var state = State();
        var runtime = Runtime(state, integration: new VirtualIntegrationOptions { MaximumMissions = 1 });
        var result = runtime.Preflight(Profile("reject", CapacityProfileKind.Peak, 2, 2, 2, 60_000));
        Assert.False(result.Accepted);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void EightHourVirtualSoak_CompletesWithConservationAndBoundedState()
    {
        var report = Runtime(State()).Run(Profile("eight", CapacityProfileKind.EightHourVirtualSoak, 12, 4, 2, 28_800_000), Start);
        Assert.True(report.Admission.Accepted);
        Assert.True(report.Profile.ConservationSatisfied);
        Assert.True(report.Profile.BoundedStateSatisfied);
        Assert.True(report.ResourceBudgetSatisfied);
        Assert.Equal(12, report.Profile.SampleCount);
        Assert.False(string.IsNullOrWhiteSpace(report.Profile.FinalStateHash));
    }

    [Fact]
    public void TwentyFourHourVirtualSoak_CompletesWithConservationAndBoundedState()
    {
        var report = Runtime(State()).Run(Profile("day", CapacityProfileKind.TwentyFourHourVirtualSoak, 18, 6, 2, 86_400_000), Start);
        Assert.True(report.Profile.ConservationSatisfied);
        Assert.True(report.Profile.BoundedStateSatisfied);
        Assert.True(report.ResourceBudgetSatisfied);
        Assert.Equal(18, report.Profile.SampleCount);
    }

    [Fact]
    public void SameProfileAndInputs_ProduceIdenticalFinalStateHash()
    {
        var first = Runtime(State()).Run(Profile("det", CapacityProfileKind.Peak, 8, 4, 2, 120_000), Start);
        var second = Runtime(State()).Run(Profile("det", CapacityProfileKind.Peak, 8, 4, 2, 120_000), Start);
        Assert.Equal(first.Profile.FinalStateHash, second.Profile.FinalStateHash);
        Assert.Equal(first.Samples.Select(x => x.StateHash), second.Samples.Select(x => x.StateHash));
    }

    [Fact]
    public void CanonicalCheckpointRestore_PreservesCompletedProfileEvidence()
    {
        var state = State();
        var runtime = Runtime(state);
        var report = runtime.Run(Profile("restore", CapacityProfileKind.Nominal, 6, 2, 2, 60_000), Start);
        var json = state.ToCanonicalJson();
        var restoredState = SimulationStateStore.FromCanonicalJson(json, EngineOptions());
        var restored = Runtime(restoredState).TryGetProfileResult("restore");
        Assert.NotNull(restored);
        Assert.Equal(report.Profile.FinalStateHash, restored!.FinalStateHash);
        Assert.Equal(state.ComputeHash(), restoredState.ComputeHash());
    }

    [Fact]
    public void Samples_AreBoundedByConfiguredLimit()
    {
        var options = CapacityOptions();
        options.MaximumSamplesPerProfile = 3;
        var report = Runtime(State(), capacity: options).Run(Profile("samples", CapacityProfileKind.Peak, 10, 2, 2, 60_000), Start);
        Assert.Equal(3, report.Samples.Count);
    }

    [Fact]
    public void ProfileRegistry_IsBounded()
    {
        var options = CapacityOptions();
        options.MaximumProfiles = 1;
        var state = State();
        var runtime = Runtime(state, capacity: options);
        runtime.Run(Profile("one", CapacityProfileKind.Nominal, 1, 1, 1, 30_000), Start);
        var next = runtime.Preflight(Profile("two", CapacityProfileKind.Nominal, 1, 1, 1, 30_000));
        Assert.False(next.Accepted);
    }

    [Fact]
    public void HilReadiness_CanBeSoftwareReadyWhileRealHilAndSiteAcceptanceRemainFalse()
    {
        var runtime = Runtime(State());
        var eight = runtime.Run(Profile("h8", CapacityProfileKind.EightHourVirtualSoak, 4, 2, 2, 28_800_000), Start);
        var day = runtime.Run(Profile("h24", CapacityProfileKind.TwentyFourHourVirtualSoak, 4, 2, 2, 86_400_000), Start.AddDays(1));
        var gate = runtime.BuildHilReadiness(eight, day, true, true, true, true, true);
        Assert.True(gate.ReadyToEnterS9);
        Assert.False(gate.RealHilExecuted);
        Assert.False(gate.MechanicalSafetyAccepted);
        Assert.False(gate.SiteAccepted);
        Assert.Equal(3, gate.MissingExternalPrerequisites.Count);
    }

    [Fact]
    public void HilReadiness_FailsClosedWhenAnySoftwarePrerequisiteIsMissing()
    {
        var runtime = Runtime(State());
        var eight = runtime.Run(Profile("f8", CapacityProfileKind.EightHourVirtualSoak, 2, 1, 2, 28_800_000), Start);
        var day = runtime.Run(Profile("f24", CapacityProfileKind.TwentyFourHourVirtualSoak, 2, 1, 2, 86_400_000), Start.AddDays(1));
        var gate = runtime.BuildHilReadiness(eight, day, true, true, true, false, true);
        Assert.False(gate.ReadyToEnterS9);
    }

    [Fact]
    public void ResourceEvidence_ContainsRssGcThreadAndHandleMetrics()
    {
        var report = Runtime(State()).Run(Profile("resource", CapacityProfileKind.Nominal, 3, 1, 2, 30_000), Start);
        Assert.True(report.RssBeforeBytes > 0);
        Assert.True(report.RssAfterBytes > 0);
        Assert.True(report.Gen0Collections >= 0 && report.Gen1Collections >= 0 && report.Gen2Collections >= 0);
        Assert.True(report.ThreadCount > 0);
        Assert.True(report.HandleCount >= -1);
    }

    [Fact]
    public void Preflight_RejectsRouteLookAheadOverflow()
    {
        var traffic = TrafficOptions();
        traffic.MaximumRollingLookAheadSegments = 1;
        var state = State();
        var result = Runtime(state, traffic: traffic).Preflight(Profile("route", CapacityProfileKind.Peak, 1, 1, 2, 10_000));
        Assert.False(result.Accepted);
        Assert.Equal(0, state.Count);
    }

    private static CapacityProfileDefinition Profile(string id, CapacityProfileKind kind, int missions, int concurrent, int segments, long duration) =>
        new(id, kind, missions, concurrent, segments, duration);

    private static CapacityReadinessRuntime Runtime(
        SimulationStateStore state,
        CapacityReadinessOptions? capacity = null,
        VirtualIntegrationOptions? integration = null,
        VirtualTrafficOptions? traffic = null) =>
        new(state, EngineOptions(), capacity ?? CapacityOptions(), integration ?? IntegrationOptions(), PlcOptions(), RgvOptions(), traffic ?? TrafficOptions(), ExternalOptions(), HealthOptions());

    private static SimulationStateStore State() => new(EngineOptions());
    private static SimulationScenarioEngineOptions EngineOptions() => new() { MaximumStateEntries = 50_000, MaximumStateValueCharacters = 16_384, MaximumCheckpointBytes = 64 * 1024 * 1024, MaximumTimelineItems = 500_000, MaximumSpeedFactor = 10_000 };
    private static CapacityReadinessOptions CapacityOptions() => new() { MaximumMissionsPerProfile = 64, MaximumConcurrentMissions = 16, MaximumSegmentsPerMission = 8, MaximumSamplesPerProfile = 128, MaximumProfiles = 16, MaximumWallClockMilliseconds = 120_000, MaximumRssGrowthBytes = 268_435_456 };
    private static VirtualIntegrationOptions IntegrationOptions() => new() { MaximumMissions = 128, MaximumSegmentsPerMission = 16, MaximumAuditRecords = 20_000, ReservationLeaseMilliseconds = 60_000, ExternalAckMaximumAttempts = 3, ExternalAckTimeoutMilliseconds = 5_000, ExternalAckRetryDelayMilliseconds = 1_000 };
    private static VirtualPlcOptions PlcOptions() => new() { MaximumBlocks = 128, MaximumBlockBytes = 65_536, MaximumOperationBytes = 65_536, MaximumScenarioTransferBytes = 1_536, MaximumFaults = 1_024, MaximumFaultPayloadBytes = 1_536, MaximumAuditRecords = 20_000 };
    private static VirtualRgvOptions RgvOptions() => new() { MaximumVehicles = 128, MaximumSegments = 2_048, MaximumRouteSegments = 64, MaximumAuditRecords = 20_000 };
    private static VirtualTrafficOptions TrafficOptions() => new() { MaximumZones = 2_048, MaximumSegmentsPerZone = 16, MaximumReservations = 4_096, MaximumWaitingRequests = 4_096, MaximumDeadlocks = 1_024, MaximumAuditRecords = 20_000, MaximumRollingLookAheadSegments = 64, DefaultReservationLeaseMilliseconds = 60_000, MaximumReservationLeaseMilliseconds = 604_800_000 };
    private static VirtualExternalOptions ExternalOptions() => new() { MaximumEndpoints = 128, MaximumFaults = 1_024, MaximumRequests = 2_048, MaximumAuditRecords = 20_000, MaximumRetryAttempts = 8, DefaultTimeoutMilliseconds = 5_000, MaximumDelayMilliseconds = 604_800_000, CircuitFailureThreshold = 5, CircuitOpenMilliseconds = 30_000 };
    private static VirtualHealthOptions HealthOptions() => new() { MaximumAssets = 128, MaximumSamplesPerAsset = 1_000, MaximumForecastsPerAsset = 128, MaximumOutcomesPerAsset = 64, MaximumGeneratedSamplesPerAction = 1_000, MaximumAuditRecords = 20_000, ForecastMinimumHistoryPoints = 48, ForecastMinimumHistorySpanHours = 24, ForecastMaximumHistoryPoints = 1_000, TrendWindowSize = 48, TrendChangeThreshold = 2, HealthyMinimumScore = 85, AttentionMinimumScore = 70, DegradedMinimumScore = 40, MaximumRulHours = 17_520 };
}
