namespace Wcs.Core.Tests;

using System.Collections.Concurrent;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

public sealed class PlcMachineLearningTests
{
    [Fact]
    public void Isolation_forest_separates_obvious_outliers_from_normal_windows()
    {
        var profile = CreateProfile();
        profile.MinimumTrainingWindows = 100;
        profile.TreeCount = 120;
        profile.SampleSize = 128;
        profile.Contamination = 0.02;
        var vectors = Enumerable.Range(0, 500)
            .Select(index => TrainingVector(5 + Math.Sin(index * 0.17) * 0.25, index))
            .ToArray();

        var model = IsolationForest.Train(profile, vectors, DateTime.UtcNow);
        var normalScore = IsolationForest.Score(model, TrainingVector(5.1, 999).Values);
        var anomalyScore = IsolationForest.Score(model, TrainingVector(20, 1000).Values);

        Assert.True(anomalyScore > normalScore + 0.08, $"normal={normalScore}, anomaly={anomalyScore}");
        Assert.True(anomalyScore >= model.DecisionThreshold);
    }

    [Fact]
    public void Feature_window_emits_deterministic_numeric_features()
    {
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            Profiles = new List<PlcMlProfile> { CreateProfile() }
        };
        var engine = new PlcFeatureWindowEngine(options);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        engine.Process(Sample(4, start.AddMilliseconds(100)));
        engine.Process(Sample(5, start.AddMilliseconds(300)));
        engine.Process(Sample(6, start.AddMilliseconds(700)));
        var vectors = engine.FlushExpired(start.AddSeconds(1.1));

        var vector = Assert.Single(vectors);
        Assert.Equal("CV-MOTOR", vector.ProfileId);
        Assert.Equal(8, vector.Values.Length);
        Assert.Equal("Current.mean", vector.FeatureNames[0]);
        Assert.Equal(5, vector.Values[0], 6);
        Assert.Equal(4, vector.Values[2], 6);
        Assert.Equal(6, vector.Values[3], 6);
        Assert.Equal(6, vector.Values[4], 6);
        Assert.Equal(2, vector.Values[6], 6);
        Assert.Equal(3, vector.SourceSampleCount);
    }

    [Fact]
    public async Task Ml_engine_publishes_one_lifecycle_and_recovers_after_normal_window()
    {
        var profile = CreateProfile();
        profile.ConsecutiveAbnormalCount = 1;
        profile.ConsecutiveRecoveryCount = 1;
        profile.MinimumTrainingWindows = 100;
        profile.ObserveThreshold = 0.55;
        var training = Enumerable.Range(0, 500)
            .Select(index => TrainingVector(5 + Math.Sin(index * 0.13) * 0.2, index))
            .ToArray();
        var model = IsolationForest.Train(profile, training, DateTime.UtcNow);
        var options = new PlcMlAnomalyOptions
        {
            Enabled = true,
            InactiveInferenceStateRetentionSeconds = 10,
            Profiles = new List<PlcMlProfile> { profile }
        };

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

        var modelStore = new MemoryModelStore(model);
        var engine = new PlcMlAnomalyEngine(
            options,
            new PlcFeatureWindowEngine(options),
            modelStore,
            new MemoryTrainingStore(training),
            eventBus);
        await engine.InitializeAsync();

        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await engine.ProcessAsync(Sample(20, start.AddMilliseconds(100)));
        await engine.ProcessAsync(Sample(21, start.AddMilliseconds(300)));
        await engine.ProcessAsync(Sample(22, start.AddMilliseconds(700)));
        await engine.MaintenanceAsync(start.AddSeconds(1.1));

        var anomaly = Assert.Single(detected).Anomaly;
        Assert.Equal(PlcAnomalyType.MachineLearning, anomaly.Type);
        Assert.Equal("IsolationForest", anomaly.DetectorName);
        Assert.Equal(model.Version, anomaly.ModelVersion);

        await engine.ProcessAsync(Sample(5, start.AddSeconds(1.1)));
        await engine.ProcessAsync(Sample(5.1, start.AddSeconds(1.3)));
        await engine.ProcessAsync(Sample(4.9, start.AddSeconds(1.7)));
        await engine.MaintenanceAsync(start.AddSeconds(2.1));

        Assert.Single(recovered);
        var status = Assert.Single(engine.GetStatus());
        Assert.Equal(1, status.Raised);
        Assert.Equal(1, status.Recovered);
        Assert.Equal(0, status.ActiveAnomalies);
        Assert.Equal(0, status.Failures);
    }

    private static PlcMlProfile CreateProfile() => new()
    {
        ProfileId = "CV-MOTOR",
        PlcPattern = "PLC-TEST",
        DevicePattern = "CV01",
        WindowSeconds = 1,
        MinimumSamplesPerSignal = 3,
        MinimumTrainingWindows = 100,
        MaximumTrainingWindows = 10_000,
        TreeCount = 100,
        SampleSize = 128,
        Contamination = 0.02,
        ObserveThreshold = 0.60,
        WarningThreshold = 0.70,
        AlarmThreshold = 0.85,
        ConsecutiveAbnormalCount = 1,
        ConsecutiveRecoveryCount = 1,
        RaiseAlarm = false,
        Signals = new List<PlcMlSignalDefinition>
        {
            new()
            {
                Name = "Current",
                Pattern = "CV01_Current",
                Kind = PlcMlSignalKind.Numeric
            }
        }
    };

    private static PlcFeatureVector TrainingVector(double value, int index) => new()
    {
        ProfileId = "CV-MOTOR",
        PlcName = "PLC-TEST",
        DeviceId = "CV01",
        WindowStartUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index),
        WindowEndUtc = new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc).AddSeconds(index),
        FeatureNames = new[]
        {
            "Current.mean",
            "Current.stddev",
            "Current.min",
            "Current.max",
            "Current.last",
            "Current.slope",
            "Current.range",
            "Current.samplesPerSecond"
        },
        Values = new[]
        {
            value,
            0.08 + Math.Abs(Math.Sin(index * 0.11)) * 0.04,
            value - 0.1,
            value + 0.1,
            value + Math.Sin(index * 0.07) * 0.05,
            Math.Sin(index * 0.05) * 0.1,
            0.2,
            3.0
        },
        SourceSampleCount = 3
    };

    private static PlcAnomalySample Sample(double value, DateTime timestampUtc) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TimestampUtc = timestampUtc,
        PlcName = "PLC-TEST",
        DbBlock = 1,
        DeviceId = "CV01",
        SignalName = "CV01_Current",
        NewValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NumericValue = value
    };

    private sealed class MemoryModelStore : IPlcMlModelStore
    {
        private PlcIsolationForestModel? _model;
        public MemoryModelStore(PlcIsolationForestModel model) => _model = model;
        public Task<PlcIsolationForestModel?> LoadActiveAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_model?.ProfileId == profileId ? _model : null);
        public Task SaveAndActivateAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default)
        {
            _model = model;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryTrainingStore : IPlcMlTrainingStore
    {
        private readonly List<PlcFeatureVector> _vectors;
        public MemoryTrainingStore(IEnumerable<PlcFeatureVector> vectors) => _vectors = vectors.ToList();
        public Task<int> CountAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_vectors.Count);
        public Task AppendAsync(PlcFeatureVector vector, int maximumWindows, CancellationToken cancellationToken = default)
        {
            if (_vectors.Count < maximumWindows) _vectors.Add(vector);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PlcFeatureVector>> ReadAsync(string profileId, int maximumWindows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlcFeatureVector>>(_vectors.TakeLast(maximumWindows).ToArray());
    }
}
