namespace Wcs.Core.AnomalyDetection.MachineLearning.Adapters;

using System.Security.Cryptography;
using System.Text;
using Wcs.Core.AnomalyDetection.MachineLearning;

public enum PlcMlModelAdapterKind
{
    IsolationForest = 0,
    Onnx = 1
}

public enum PlcMlScoreTransform
{
    Identity = 0,
    Sigmoid = 1,
    OneMinus = 2
}

/// <summary>
/// Versioned, approved metadata for one locally deployed inference artifact.
/// The manifest never contains secrets or a remote download URL.
/// </summary>
public sealed class PlcMlModelManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ProfileId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public PlcMlModelAdapterKind AdapterKind { get; set; }
    public string AdapterId { get; set; } = string.Empty;
    public string ArtifactFile { get; set; } = string.Empty;
    public string ArtifactSha256 { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? ApprovedAtUtc { get; set; }
    public string[] FeatureNames { get; set; } = Array.Empty<string>();
    public double[] Means { get; set; } = Array.Empty<double>();
    public double[] StandardDeviations { get; set; } = Array.Empty<double>();
    public string InputName { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public int[] InputShape { get; set; } = Array.Empty<int>();
    public PlcMlScoreTransform ScoreTransform { get; set; }
    public double DecisionThreshold { get; set; } = 0.5;
    public double CalibrationMeanScore { get; set; }
    public double CalibrationP95Score { get; set; }
    public string? Description { get; set; }
}

public sealed record PlcMlModelArtifact
{
    public required PlcMlModelManifest Manifest { get; init; }
    public required ReadOnlyMemory<byte> Content { get; init; }
}

public sealed record PlcMlAdapterPrediction
{
    public required string ProfileId { get; init; }
    public required string ModelVersion { get; init; }
    public required PlcMlModelAdapterKind AdapterKind { get; init; }
    public required string AdapterId { get; init; }
    public required string DetectorName { get; init; }
    public required double Score { get; init; }
    public required double DecisionThreshold { get; init; }
    public required bool IsAnomaly { get; init; }
    public required string Explanation { get; init; }
    public double CalibrationMeanScore { get; init; }
    public double CalibrationP95Score { get; init; }
}

public interface IPlcMlModelRuntime : IDisposable
{
    PlcMlModelManifest Manifest { get; }
    PlcMlAdapterPrediction Predict(PlcFeatureVector vector);
}

public interface IPlcMlModelAdapter
{
    PlcMlModelAdapterKind Kind { get; }
    string AdapterId { get; }
    Task<IPlcMlModelRuntime> LoadAsync(
        PlcMlProfile profile,
        PlcMlModelArtifact artifact,
        CancellationToken cancellationToken = default);
}

public interface IPlcMlExternalModelStore
{
    Task<PlcMlModelArtifact?> LoadActiveAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task<PlcMlModelArtifact?> LoadVersionAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlcMlModelManifest>> ListAsync(
        string profileId,
        CancellationToken cancellationToken = default);

    Task ActivateAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default);
}

public sealed class PlcMlModelAdapterRegistry
{
    private readonly IReadOnlyDictionary<PlcMlModelAdapterKind, IPlcMlModelAdapter> _adapters;

    public PlcMlModelAdapterRegistry(IEnumerable<IPlcMlModelAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var map = new Dictionary<PlcMlModelAdapterKind, IPlcMlModelAdapter>();
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!map.TryAdd(adapter.Kind, adapter))
                throw new InvalidOperationException($"Duplicate PLC ML adapter kind: {adapter.Kind}.");
            if (string.IsNullOrWhiteSpace(adapter.AdapterId))
                throw new InvalidOperationException($"PLC ML adapter {adapter.Kind} must provide AdapterId.");
        }
        _adapters = map;
    }

    public IReadOnlyCollection<PlcMlModelAdapterKind> Kinds => _adapters.Keys.ToArray();

    public IPlcMlModelAdapter Resolve(PlcMlModelAdapterKind kind) =>
        _adapters.TryGetValue(kind, out var adapter)
            ? adapter
            : throw new KeyNotFoundException($"PLC ML adapter is not registered: {kind}.");
}

public static class PlcMlModelManifestValidator
{
    public const int CurrentSchemaVersion = 1;

    public static void Validate(
        PlcMlProfile profile,
        PlcMlModelManifest manifest,
        int maximumFeatureCount = 10_000)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException($"Unsupported PLC ML manifest schema: {manifest.SchemaVersion}.");
        if (!string.Equals(profile.ProfileId, manifest.ProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("Manifest ProfileId does not match the configured profile.");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("Manifest Version is required.");
        if (string.IsNullOrWhiteSpace(manifest.AdapterId))
            throw new InvalidOperationException("Manifest AdapterId is required.");
        if (string.IsNullOrWhiteSpace(manifest.ArtifactFile) || Path.IsPathRooted(manifest.ArtifactFile))
            throw new InvalidOperationException("Manifest ArtifactFile must be a relative local file name.");
        if (manifest.ArtifactFile.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Manifest ArtifactFile cannot traverse directories.");
        if (!IsSha256(manifest.ArtifactSha256))
            throw new InvalidOperationException("Manifest ArtifactSha256 must be a 64-character hexadecimal SHA-256 value.");
        if (manifest.CreatedUtc == default)
            throw new InvalidOperationException("Manifest CreatedUtc is required.");
        if (string.IsNullOrWhiteSpace(manifest.Source) ||
            string.IsNullOrWhiteSpace(manifest.ApprovedBy) ||
            manifest.ApprovedAtUtc is null)
            throw new InvalidOperationException("Manifest Source and approval metadata are required.");
        if (manifest.FeatureNames.Length == 0 || manifest.FeatureNames.Length > maximumFeatureCount)
            throw new InvalidOperationException("Manifest FeatureNames count is outside the allowed range.");
        if (manifest.FeatureNames.Any(string.IsNullOrWhiteSpace) ||
            manifest.FeatureNames.Distinct(StringComparer.Ordinal).Count() != manifest.FeatureNames.Length)
            throw new InvalidOperationException("Manifest FeatureNames must be non-empty and unique.");
        if (manifest.Means.Length != manifest.FeatureNames.Length ||
            manifest.StandardDeviations.Length != manifest.FeatureNames.Length)
            throw new InvalidOperationException("Manifest normalization arrays must match FeatureNames length.");
        if (manifest.Means.Any(static value => !double.IsFinite(value)) ||
            manifest.StandardDeviations.Any(static value => !double.IsFinite(value) || value <= 0))
            throw new InvalidOperationException("Manifest normalization values must be finite and standard deviations must be positive.");
        if (string.IsNullOrWhiteSpace(manifest.InputName) || string.IsNullOrWhiteSpace(manifest.OutputName))
            throw new InvalidOperationException("Manifest input and output names are required.");
        if (manifest.InputShape.Length != 2 ||
            manifest.InputShape[0] is not (-1 or 1) ||
            manifest.InputShape[1] != manifest.FeatureNames.Length)
            throw new InvalidOperationException("Manifest InputShape must be [-1|1, featureCount].");
        if (!double.IsFinite(manifest.DecisionThreshold) || manifest.DecisionThreshold is < 0 or > 1)
            throw new InvalidOperationException("Manifest DecisionThreshold must be between 0 and 1.");
        if (!double.IsFinite(manifest.CalibrationMeanScore) ||
            !double.IsFinite(manifest.CalibrationP95Score) ||
            manifest.CalibrationMeanScore is < 0 or > 1 ||
            manifest.CalibrationP95Score is < 0 or > 1)
            throw new InvalidOperationException("Manifest calibration scores must be between 0 and 1.");
    }

    public static void VerifyArtifactHash(PlcMlModelManifest manifest, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var actual = Convert.ToHexString(SHA256.HashData(content));
        if (!string.Equals(actual, manifest.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"PLC ML artifact hash mismatch. Expected={manifest.ArtifactSha256}; Actual={actual}.");
    }

    public static string ComputeManifestHash(PlcMlModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = string.Join('|',
            manifest.SchemaVersion,
            manifest.ProfileId,
            manifest.Version,
            (int)manifest.AdapterKind,
            manifest.AdapterId,
            manifest.ArtifactFile,
            manifest.ArtifactSha256.ToUpperInvariant(),
            manifest.CreatedUtc.ToUniversalTime().ToString("O"),
            manifest.Source,
            manifest.ApprovedBy,
            manifest.ApprovedAtUtc?.ToUniversalTime().ToString("O"),
            string.Join('\u001f', manifest.FeatureNames),
            string.Join('\u001f', manifest.Means.Select(static value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            string.Join('\u001f', manifest.StandardDeviations.Select(static value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
            manifest.InputName,
            manifest.OutputName,
            string.Join(',', manifest.InputShape),
            (int)manifest.ScoreTransform,
            manifest.DecisionThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            manifest.CalibrationMeanScore.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            manifest.CalibrationP95Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character => Uri.IsHexDigit(character));
}

public static class PlcMlScoreTransformer
{
    public static double Apply(double value, PlcMlScoreTransform transform)
    {
        if (!double.IsFinite(value))
            throw new InvalidOperationException("Model output score is not finite.");
        var transformed = transform switch
        {
            PlcMlScoreTransform.Identity => value,
            PlcMlScoreTransform.Sigmoid => 1d / (1d + Math.Exp(-Math.Clamp(value, -60, 60))),
            PlcMlScoreTransform.OneMinus => 1d - value,
            _ => throw new ArgumentOutOfRangeException(nameof(transform), transform, "Unsupported score transform.")
        };
        if (!double.IsFinite(transformed) || transformed is < 0 or > 1)
            throw new InvalidOperationException($"Transformed model score must be between 0 and 1, actual={transformed}.");
        return transformed;
    }
}
