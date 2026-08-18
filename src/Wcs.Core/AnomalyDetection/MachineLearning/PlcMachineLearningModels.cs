namespace Wcs.Core.AnomalyDetection.MachineLearning;

using Wcs.Core.AnomalyDetection;

public enum PlcMlSignalKind
{
    Numeric = 0,
    Boolean = 1
}

public sealed class PlcMlSignalDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public PlcMlSignalKind Kind { get; set; }
}

/// <summary>不进入 Isolation Forest 特征，只用于区分运行模式、产品、负载等上下文。</summary>
public sealed class PlcMlContextSignalDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = "UNKNOWN";
    public int MaximumAgeSeconds { get; set; } = 300;
}

/// <summary>一类设备的训练和推理配置，一个 Profile 可匹配多台同构设备。</summary>
public sealed class PlcMlProfile
{
    public string ProfileId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string PlcPattern { get; set; } = "*";
    public string DevicePattern { get; set; } = "*";
    public int WindowSeconds { get; set; } = 10;
    public int MinimumSamplesPerSignal { get; set; } = 3;

    public bool CollectTrainingData { get; set; }
    /// <summary>
    /// 活动模型存在时是否仍允许把在线窗口追加到正常训练池。默认 false，避免故障窗口污染基线。
    /// 仅在受控的重新采集窗口中临时启用。
    /// </summary>
    public bool CollectTrainingDataWhileModelActive { get; set; }
    public bool AutoTrain { get; set; }
    public int MinimumTrainingWindows { get; set; } = 500;
    public int MaximumTrainingWindows { get; set; } = 50_000;

    public int TreeCount { get; set; } = 100;
    public int SampleSize { get; set; } = 256;
    public double Contamination { get; set; } = 0.01;
    public int RandomSeed { get; set; } = 20260725;

    public double ObserveThreshold { get; set; } = 0.65;
    public double WarningThreshold { get; set; } = 0.75;
    public double AlarmThreshold { get; set; } = 0.85;
    public int ConsecutiveAbnormalCount { get; set; } = 3;
    public int ConsecutiveRecoveryCount { get; set; } = 5;
    public PlcAnomalySeverity Severity { get; set; } = PlcAnomalySeverity.Warning;
    public bool RaiseAlarm { get; set; } = true;

    /// <summary>默认影子运行，只有 Active 或命中 Canary 的设备才进入正式异常生命周期。</summary>
    public PlcMlDeploymentMode DeploymentMode { get; set; } = PlcMlDeploymentMode.Shadow;
    public int CanaryPercentage { get; set; }
    public bool RequireModelApproval { get; set; } = true;

    public int DriftWindowSize { get; set; } = 500;
    public int MinimumDriftSamples { get; set; } = 100;
    public double DriftWarningRatio { get; set; } = 0.15;
    public double DriftCriticalRatio { get; set; } = 0.30;
    public int DriftSnapshotIntervalSeconds { get; set; } = 60;

    public List<PlcMlSignalDefinition> Signals { get; set; } = new();
    public List<PlcMlContextSignalDefinition> ContextSignals { get; set; } = new();

    /// <summary>同一上下文、同一窗口内的同类设备 Robust Median/MAD 横向对比。</summary>
    public bool PeerComparisonEnabled { get; set; }
    public int MinimumPeerDevices { get; set; } = 5;
    public int PeerBucketWaitMs { get; set; } = 1_000;
    public int PeerBucketRetentionSeconds { get; set; } = 120;
    public double PeerMadMultiplier { get; set; } = 6.0;
    public double MinimumPeerMad { get; set; } = 0.01;
    public int ConsecutivePeerAbnormalCount { get; set; } = 2;
    public int ConsecutivePeerRecoveryCount { get; set; } = 3;
    public PlcAnomalySeverity PeerSeverity { get; set; } = PlcAnomalySeverity.Warning;
    public bool PeerRaiseAlarm { get; set; } = true;
}

public sealed class PlcMlAnomalyOptions
{
    public bool Enabled { get; set; }
    /// <summary>训练、数据集、人工复核、审批、版本列表和激活 API 的独立开关。默认关闭。</summary>
    public bool ManagementApiEnabled { get; set; }
    public string ModelDirectory { get; set; } = "data/anomaly-models";
    public string TrainingDirectory { get; set; } = "data/anomaly-training";
    public int MaintenanceIntervalMs { get; set; } = 1_000;
    public int MaximumTrackedWindows { get; set; } = 20_000;
    public int InactiveInferenceStateRetentionSeconds { get; set; } = 300;
    public List<PlcMlProfile> Profiles { get; set; } = new();
}

public sealed record PlcFeatureVector
{
    public required string ProfileId { get; init; }
    public required string PlcName { get; init; }
    public required string DeviceId { get; init; }
    public required DateTime WindowStartUtc { get; init; }
    public required DateTime WindowEndUtc { get; init; }
    public required string[] FeatureNames { get; init; }
    public required double[] Values { get; init; }
    public int SourceSampleCount { get; init; }
    public string ContextKey { get; init; } = "default";
}

public sealed class IsolationForestNode
{
    public int FeatureIndex { get; set; } = -1;
    public double SplitValue { get; set; }
    public int SampleCount { get; set; }
    public IsolationForestNode? Left { get; set; }
    public IsolationForestNode? Right { get; set; }
    public bool IsLeaf => FeatureIndex < 0 || Left is null || Right is null;
}

public sealed class PlcIsolationForestModel
{
    public string ProfileId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string[] FeatureNames { get; set; } = Array.Empty<string>();
    public double[] Means { get; set; } = Array.Empty<double>();
    public double[] StandardDeviations { get; set; } = Array.Empty<double>();
    public IsolationForestNode[] Trees { get; set; } = Array.Empty<IsolationForestNode>();
    public int TrainingSampleCount { get; set; }
    public int CalibrationSampleCount { get; set; }
    public int SubsampleSize { get; set; }
    public double DecisionThreshold { get; set; }
    public double Contamination { get; set; }
    public double CalibrationMeanScore { get; set; }
    public double CalibrationP95Score { get; set; }
}

public sealed record PlcMlModelVersionInfo
{
    public required string ProfileId { get; init; }
    public required string Version { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required int TrainingSampleCount { get; init; }
    public required int CalibrationSampleCount { get; init; }
    public required int TreeCount { get; init; }
    public required double DecisionThreshold { get; init; }
    public required bool IsActive { get; init; }
}

public sealed record PlcMlPrediction
{
    public required string ProfileId { get; init; }
    public required string ModelVersion { get; init; }
    public required double Score { get; init; }
    public required double DecisionThreshold { get; init; }
    public required bool IsAnomaly { get; init; }
    public required string Explanation { get; init; }
}

public sealed record PlcMlTrainingResult
{
    public required string ProfileId { get; init; }
    public required string ModelVersion { get; init; }
    public string? DatasetVersion { get; init; }
    public required int TrainingSampleCount { get; init; }
    public required int CalibrationSampleCount { get; init; }
    public required int TreeCount { get; init; }
    public required double DecisionThreshold { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public PlcMlApprovalStatus ApprovalStatus { get; init; }
    public bool Activated { get; init; }
}

public sealed record PlcMlProfileStatus
{
    public required string ProfileId { get; init; }
    public bool Enabled { get; init; }
    public PlcMlDeploymentMode DeploymentMode { get; init; }
    public int CanaryPercentage { get; init; }
    public string? ActiveModelVersion { get; init; }
    public int TrainingWindowCount { get; init; }
    public long CompletedWindows { get; init; }
    public long DroppedIncompleteWindows { get; init; }
    public long Predictions { get; init; }
    public long AnomalyObservations { get; init; }
    public long Raised { get; init; }
    public long Recovered { get; init; }
    public long ShadowRaised { get; init; }
    public long ActiveRaised { get; init; }
    public int ActiveAnomalies { get; init; }
    public int TrackedWindows { get; init; }
    public int TrackedInferenceStates { get; init; }
    public int TrackedContextDevices { get; init; }
    public long PeerBucketsEvaluated { get; init; }
    public long PeerDevicesEvaluated { get; init; }
    public long PeerRaised { get; init; }
    public long PeerRecovered { get; init; }
    public long PeerShadowRaised { get; init; }
    public long PeerActiveRaised { get; init; }
    public long PeerSkippedBuckets { get; init; }
    public int TrackedPeerBuckets { get; init; }
    public int TrackedPeerStates { get; init; }
    public PlcMlDriftStatus DriftStatus { get; init; }
    public double DriftRatio { get; init; }
    public int DriftSampleCount { get; init; }
    public long Failures { get; init; }
    public string? LastError { get; init; }
}

public sealed record PlcMlPeerStatus
{
    public long BucketsEvaluated { get; init; }
    public long DevicesEvaluated { get; init; }
    public long Raised { get; init; }
    public long Recovered { get; init; }
    public long ShadowRaised { get; init; }
    public long ActiveRaised { get; init; }
    public long SkippedBuckets { get; init; }
    public long Failures { get; init; }
    public int TrackedBuckets { get; init; }
    public int TrackedStates { get; init; }
}

public interface IPlcMlModelStore
{
    Task<PlcIsolationForestModel?> LoadActiveAsync(string profileId, CancellationToken cancellationToken = default);
    Task<PlcIsolationForestModel?> LoadVersionAsync(string profileId, string version, CancellationToken cancellationToken = default);
    Task SaveVersionAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default);
    Task ActivateAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default);
    Task SaveAndActivateAsync(PlcIsolationForestModel model, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlModelVersionInfo>> ListAsync(string profileId, CancellationToken cancellationToken = default);
}

public interface IPlcMlTrainingStore
{
    Task<int> CountAsync(string profileId, CancellationToken cancellationToken = default);
    Task AppendAsync(PlcFeatureVector vector, int maximumWindows, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcFeatureVector>> ReadAsync(string profileId, int maximumWindows, CancellationToken cancellationToken = default);
    Task<PlcMlDatasetInfo> CreateDatasetAsync(
        string profileId,
        int maximumWindows,
        string createdBy,
        string? description,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlDatasetInfo>> ListDatasetsAsync(string profileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcFeatureVector>> ReadDatasetAsync(
        string profileId,
        string datasetVersion,
        int maximumWindows,
        CancellationToken cancellationToken = default);
}

public interface IPlcMlAnomalyEngine
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask ProcessAsync(PlcAnomalySample sample, CancellationToken cancellationToken = default);
    Task MaintenanceAsync(DateTime utcNow, CancellationToken cancellationToken = default);
    Task<PlcMlTrainingResult> TrainAsync(string profileId, CancellationToken cancellationToken = default);
    Task<PlcMlTrainingResult> TrainAsync(
        string profileId,
        string? datasetVersion,
        string? requestedBy,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlcMlModelVersionInfo>> ListModelsAsync(string profileId, CancellationToken cancellationToken = default);
    Task<PlcMlModelVersionInfo> ActivateModelAsync(string profileId, string version, CancellationToken cancellationToken = default);
    IReadOnlyList<PlcMlProfileStatus> GetStatus();
}
