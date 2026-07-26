namespace Wcs.Core.Tests;

using System.Collections.Concurrent;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcMlPeerContextTransitionTests
{
    [Fact]
    public async Task Context_change_resets_streak_and_recovers_old_context_lifecycle()
    {
        var profile = new PlcMlProfile
        {
            ProfileId = "PEER-CONTEXT",
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
            ConsecutivePeerAbnormalCount = 2,
            ConsecutivePeerRecoveryCount = 3,
            PeerRaiseAlarm = false,
            Signals = new List<PlcMlSignalDefinition>
            {
                new() { Name = "Current", Pattern = "*_Current", Kind = PlcMlSignalKind.Numeric }
            }
        };
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            MaximumTrackedWindows = 1000,
            Profiles = new List<PlcMlProfile> { profile }
        };
        var store = new MemoryGovernanceStore();
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
        var engine = new PlcMlPeerComparisonEngine(options, store, eventBus);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Auto 下第一次偏离，只形成连续计数 1。
        AddWindow(engine, start, "Mode=Auto", outlierDevice: 4);
        await engine.FlushAsync(start.AddSeconds(2));
        Assert.Empty(detected);

        // 切换 Manual 后不能继承 Auto 的连续计数，因此仍不能激活。
        AddWindow(engine, start.AddSeconds(2), "Mode=Manual", outlierDevice: 4);
        await engine.FlushAsync(start.AddSeconds(4));
        Assert.Empty(detected);

        // Manual 第二次连续偏离才激活。
        AddWindow(engine, start.AddSeconds(4), "Mode=Manual", outlierDevice: 4);
        await engine.FlushAsync(start.AddSeconds(6));
        var active = Assert.Single(detected).Anomaly;
        Assert.Contains("Mode=Manual", active.AnomalyKey, StringComparison.Ordinal);
        Assert.True(Assert.Single(store.Candidates).IsActive);

        // 再切回 Auto：旧 Manual 生命周期应立即恢复，新 Auto 只重新形成计数 1。
        AddWindow(engine, start.AddSeconds(6), "Mode=Auto", outlierDevice: 4);
        await engine.FlushAsync(start.AddSeconds(8));

        Assert.Single(detected);
        var recovery = Assert.Single(recovered).Anomaly;
        Assert.Equal(active.AnomalyId, recovery.AnomalyId);
        Assert.Equal(start.AddSeconds(6), recovery.EndTimeUtc);
        Assert.False(Assert.Single(store.Candidates).IsActive);
        var status = engine.GetStatus(profile.ProfileId);
        Assert.Equal(1, status.Raised);
        Assert.Equal(1, status.Recovered);
        Assert.Equal(0, status.Failures);
    }

    private static void AddWindow(
        PlcMlPeerComparisonEngine engine,
        DateTime start,
        string context,
        int outlierDevice)
    {
        for (var index = 0; index < 5; index++)
        {
            engine.Add(new PlcFeatureVector
            {
                ProfileId = "PEER-CONTEXT",
                PlcName = "PLC-1",
                DeviceId = $"CV{index:D2}",
                WindowStartUtc = start,
                WindowEndUtc = start.AddSeconds(1),
                FeatureNames = new[] { "Current.mean" },
                Values = new[] { index == outlierDevice ? 20.0 : 5.0 + index * 0.001 },
                SourceSampleCount = 3,
                ContextKey = context
            });
        }
    }

    private sealed class MemoryGovernanceStore : IPlcMlGovernanceStore
    {
        private readonly Dictionary<string, PlcMlCandidateRecord> _candidates = new(StringComparer.Ordinal);
        public IReadOnlyCollection<PlcMlCandidateRecord> Candidates => _candidates.Values;

        public Task UpsertCandidateAsync(
            PlcMlCandidateRecord candidate,
            CancellationToken cancellationToken = default)
        {
            _candidates[candidate.CandidateId] = candidate;
            return Task.CompletedTask;
        }

        public Task RecoverCandidateAsync(
            string candidateId,
            DateTime recoveredUtc,
            CancellationToken cancellationToken = default)
        {
            _candidates[candidateId] = _candidates[candidateId] with
            {
                IsActive = false,
                RecoveredUtc = recoveredUtc
            };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlcMlCandidateRecord>> QueryCandidatesAsync(
            string? profileId,
            PlcMlReviewDecision? decision,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlCandidateRecord>>(_candidates.Values.Take(maximumCount).ToArray());

        public Task<PlcMlCandidateRecord> ReviewCandidateAsync(
            string candidateId,
            PlcMlReviewDecision decision,
            string reviewedBy,
            string? comment,
            DateTime reviewedUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RegisterModelAsync(
            PlcMlModelGovernanceInfo model,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PlcMlModelGovernanceInfo?> GetModelAsync(
            string profileId,
            string version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PlcMlModelGovernanceInfo?>(null);

        public Task<IReadOnlyList<PlcMlModelGovernanceInfo>> ListModelsAsync(
            string profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcMlModelGovernanceInfo>>(Array.Empty<PlcMlModelGovernanceInfo>());

        public Task<PlcMlModelGovernanceInfo> DecideModelAsync(
            string profileId,
            string version,
            PlcMlApprovalStatus status,
            string actor,
            string? comment,
            DateTime decidedUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> IsModelApprovedAsync(
            string profileId,
            string version,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task SaveDriftSnapshotAsync(
            PlcMlDriftSnapshot snapshot,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PlcMlDriftSnapshot?> GetLatestDriftAsync(
            string profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PlcMlDriftSnapshot?>(null);

        public Task<PlcMlEvaluationSummary> GetEvaluationAsync(
            string profileId,
            string? modelVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlcMlEvaluationSummary { ProfileId = profileId });
    }
}
