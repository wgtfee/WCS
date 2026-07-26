namespace Wcs.Core.Tests;

using System.Collections.Concurrent;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcMlContextPeerTests
{
    [Fact]
    public void Context_center_resolves_values_and_expires_stale_context()
    {
        var profile = CreateProfile();
        profile.ContextSignals = new List<PlcMlContextSignalDefinition>
        {
            new() { Name = "Mode", Pattern = "*_Mode", DefaultValue = "UNKNOWN", MaximumAgeSeconds = 10 },
            new() { Name = "Product", Pattern = "*_Product", DefaultValue = "NONE", MaximumAgeSeconds = 10 }
        };
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            Profiles = new List<PlcMlProfile> { profile }
        };
        var center = new PlcMlOperatingContextCenter(options);
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        center.Update(Sample("CV01_Mode", "Auto", time));
        center.Update(Sample("CV01_Product", "FDY-A", time));

        Assert.Equal(
            "Mode=Auto|Product=FDY-A",
            center.Resolve(profile.ProfileId, "PLC-1", "CV01", time.AddSeconds(5)));
        Assert.Equal(
            "Mode=UNKNOWN|Product=NONE",
            center.Resolve(profile.ProfileId, "PLC-1", "CV01", time.AddSeconds(11)));
    }

    [Fact]
    public async Task Peer_engine_detects_one_outlier_and_recovers_it()
    {
        var profile = CreateProfile();
        profile.DeploymentMode = PlcMlDeploymentMode.Active;
        var options = Options(profile);
        var governance = new MemoryGovernanceStore();
        var eventBus = new EventBus();
        var detected = new ConcurrentBag<PlcAnomalyDetectedEvent>();
        var recovered = new ConcurrentBag<PlcAnomalyRecoveredEvent>();
        eventBus.Subscribe<PlcAnomalyDetectedEvent>((evt, _) =>
        {
            detected.Add(evt);
            return Task.CompletedTask;
        });
        eventBus.Subscribe<PlcAnomalyRecoveredEvent>((evt, _) =>
        {
            recovered.Add(evt);
            return Task.CompletedTask;
        });
        var engine = new PlcMlPeerComparisonEngine(options, governance, eventBus);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        AddPeerWindow(engine, start, "Mode=Auto", outlierDevice: 4, outlierValue: 20);
        await engine.FlushAsync(start.AddSeconds(2));

        var anomaly = Assert.Single(detected).Anomaly;
        Assert.Equal(PlcAnomalyType.ContextualPeerComparison, anomaly.Type);
        Assert.Equal("CV04", anomaly.DeviceId);
        Assert.Equal("ContextualPeerMedianMad", anomaly.DetectorName);
        Assert.True(anomaly.Score >= profile.PeerMadMultiplier);
        Assert.True(Assert.Single(governance.Candidates).RoutedToActiveLifecycle);

        AddPeerWindow(engine, start.AddSeconds(2), "Mode=Auto", outlierDevice: -1, outlierValue: 5);
        await engine.FlushAsync(start.AddSeconds(4));

        Assert.Single(recovered);
        Assert.False(Assert.Single(governance.Candidates).IsActive);
        var status = engine.GetStatus(profile.ProfileId);
        Assert.Equal(2, status.BucketsEvaluated);
        Assert.Equal(10, status.DevicesEvaluated);
        Assert.Equal(1, status.Raised);
        Assert.Equal(1, status.Recovered);
        Assert.Equal(1, status.ActiveRaised);
        Assert.Equal(0, status.Failures);
    }

    [Fact]
    public async Task Peer_engine_never_mixes_different_contexts()
    {
        var profile = CreateProfile();
        profile.MinimumPeerDevices = 5;
        var options = Options(profile);
        var governance = new MemoryGovernanceStore();
        var engine = new PlcMlPeerComparisonEngine(options, governance, new EventBus());
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var index = 0; index < 3; index++)
            engine.Add(Vector(start, $"A{index}", "Mode=Auto", index == 2 ? 50 : 5));
        for (var index = 0; index < 3; index++)
            engine.Add(Vector(start, $"M{index}", "Mode=Manual", 5));

        await engine.FlushAsync(start.AddSeconds(2));

        Assert.Empty(governance.Candidates);
        var status = engine.GetStatus(profile.ProfileId);
        Assert.Equal(0, status.BucketsEvaluated);
        Assert.Equal(2, status.SkippedBuckets);
    }

    [Fact]
    public async Task Shadow_peer_candidate_does_not_publish_formal_lifecycle()
    {
        var profile = CreateProfile();
        profile.DeploymentMode = PlcMlDeploymentMode.Shadow;
        var options = Options(profile);
        var governance = new MemoryGovernanceStore();
        var eventBus = new EventBus();
        var detected = new ConcurrentBag<PlcAnomalyDetectedEvent>();
        eventBus.Subscribe<PlcAnomalyDetectedEvent>((evt, _) =>
        {
            detected.Add(evt);
            return Task.CompletedTask;
        });
        var engine = new PlcMlPeerComparisonEngine(options, governance, eventBus);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        AddPeerWindow(engine, start, "Mode=Auto", outlierDevice: 4, outlierValue: 20);
        await engine.FlushAsync(start.AddSeconds(2));

        Assert.Empty(detected);
        var candidate = Assert.Single(governance.Candidates);
        Assert.False(candidate.RoutedToActiveLifecycle);
        Assert.Equal(PlcMlDeploymentMode.Shadow, candidate.DeploymentMode);
        var status = engine.GetStatus(profile.ProfileId);
        Assert.Equal(1, status.ShadowRaised);
        Assert.Equal(0, status.ActiveRaised);
    }

    private static void AddPeerWindow(
        PlcMlPeerComparisonEngine engine,
        DateTime start,
        string context,
        int outlierDevice,
        double outlierValue)
    {
        for (var index = 0; index < 5; index++)
            engine.Add(Vector(start, $"CV{index:D2}", context, index == outlierDevice ? outlierValue : 5));
    }

    private static PlcFeatureVector Vector(
        DateTime start,
        string deviceId,
        string context,
        double value) => new()
    {
        ProfileId = "PEER-CV",
        PlcName = "PLC-1",
        DeviceId = deviceId,
        WindowStartUtc = start,
        WindowEndUtc = start.AddSeconds(1),
        FeatureNames = new[] { "Current.mean" },
        Values = new[] { value },
        SourceSampleCount = 3,
        ContextKey = context
    };

    private static PlcMlProfile CreateProfile() => new()
    {
        ProfileId = "PEER-CV",
        Enabled = true,
        PlcPattern = "PLC-1",
        DevicePattern = "*",
        WindowSeconds = 1,
        MinimumSamplesPerSignal = 3,
        DeploymentMode = PlcMlDeploymentMode.Active,
        PeerComparisonEnabled = true,
        MinimumPeerDevices = 5,
        PeerBucketWaitMs = 0,
        PeerBucketRetentionSeconds = 10,
        PeerMadMultiplier = 6,
        MinimumPeerMad = 0.01,
        ConsecutivePeerAbnormalCount = 1,
        ConsecutivePeerRecoveryCount = 1,
        PeerRaiseAlarm = false,
        Signals = new List<PlcMlSignalDefinition>
        {
            new() { Name = "Current", Pattern = "*_Current", Kind = PlcMlSignalKind.Numeric }
        }
    };

    private static PlcMlAnomalyOptions Options(PlcMlProfile profile) => new()
    {
        Enabled = true,
        MaximumTrackedWindows = 1000,
        Profiles = new List<PlcMlProfile> { profile }
    };

    private static PlcAnomalySample Sample(string signal, string value, DateTime timestamp) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TimestampUtc = timestamp,
        PlcName = "PLC-1",
        DbBlock = 1,
        DeviceId = "CV01",
        SignalName = signal,
        NewValue = value
    };

    private sealed class MemoryGovernanceStore : IPlcMlGovernanceStore
    {
        private readonly Dictionary<string, PlcMlCandidateRecord> _candidates = new(StringComparer.Ordinal);
        public IReadOnlyCollection<PlcMlCandidateRecord> Candidates => _candidates.Values;

        public Task UpsertCandidateAsync(PlcMlCandidateRecord candidate, CancellationToken cancellationToken = default)
        {
            _candidates[candidate.CandidateId] = candidate;
            return Task.CompletedTask;
        }
        public Task RecoverCandidateAsync(string candidateId, DateTime recoveredUtc, CancellationToken cancellationToken = default)
        {
            _candidates[candidateId] = _candidates[candidateId] with
            {
                IsActive = false,
                RecoveredUtc = recoveredUtc
            };
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(string? profileId, PlcMlReviewDecision? decision, int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlCandidateRecord>>(_candidates.Values.Take(maximumCount).ToArray());
        public Task<PlcMlCandidateRecord> ReviewCandidateAsync(string candidateId, PlcMlReviewDecision decision, string reviewedBy, string? comment, DateTime reviewedUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RegisterModelAsync(PlcMlModelGovernanceInfo model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlcMlModelGovernanceInfo?> GetModelAsync(string profileId, string version, CancellationToken cancellationToken = default) => Task.FromResult<PlcMlModelGovernanceInfo?>(null);
        public Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelGovernanceInfo>>(Array.Empty<PlcMlModelGovernanceInfo>());
        public Task<PlcMlModelGovernanceInfo> DecideModelAsync(string profileId, string version, PlcMlApprovalStatus status, string actor, string? comment, DateTime decidedUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsModelApprovedAsync(string profileId, string version, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveDriftSnapshotAsync(PlcMlDriftSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult<PlcMlDriftSnapshot?>(null);
        public Task<PlcMlEvaluationSummary> GetEvaluationAsync(string profileId, string? modelVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlcMlEvaluationSummary { ProfileId = profileId });
    }
}
