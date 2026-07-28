namespace Wcs.Core.AnomalyDetection.MachineLearning.Adapters;

public sealed class PlcMlPluggableRuntimeOptions
{
    public bool Enabled { get; set; }
    public int MaximumTrackedWindows { get; set; } = 5_000;
    public int InactiveStateRetentionSeconds { get; set; } = 300;
    public List<PlcMlExternalProfileOptions> Profiles { get; set; } = new();
}

public sealed class PlcMlExternalProfileOptions
{
    public string ProfileId { get; set; } = string.Empty;
    public PlcMlModelAdapterKind AdapterKind { get; set; } = PlcMlModelAdapterKind.Onnx;
    public bool Required { get; set; } = true;
}

public sealed record PlcMlExternalRuntimeStatus
{
    public required string ProfileId { get; init; }
    public bool RuntimeEnabled { get; init; }
    public bool Required { get; init; }
    public required PlcMlModelAdapterKind ConfiguredAdapterKind { get; init; }
    public string? ActiveAdapterId { get; init; }
    public string? ActiveModelVersion { get; init; }
    public string? ManifestHash { get; init; }
    public string? ArtifactSha256 { get; init; }
    public long Predictions { get; init; }
    public long AnomalyObservations { get; init; }
    public long Raised { get; init; }
    public long Recovered { get; init; }
    public long ShadowRaised { get; init; }
    public long ActiveRaised { get; init; }
    public long Failures { get; init; }
    public int ActiveAnomalies { get; init; }
    public int TrackedInferenceStates { get; init; }
    public int CompletedWindows { get; init; }
    public int DroppedIncompleteWindows { get; init; }
    public int TrackedWindows { get; init; }
    public DateTime? LoadedUtc { get; init; }
    public string? LastError { get; init; }
}

public interface IPlcMlExternalRuntimeStatusProvider
{
    IReadOnlyList<PlcMlExternalRuntimeStatus> GetExternalRuntimeStatus();
}
