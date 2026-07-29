namespace Wcs.Infrastructure.AnomalyDetection.Forecasting;

using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Wcs.Core.AnomalyDetection.Forecasting;

public sealed class FileAssetFailureForecastModelStore : IAssetFailureForecastModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly AssetFailureForecastOptions _options;
    private readonly string _rootDirectory;

    public FileAssetFailureForecastModelStore(AssetFailureForecastOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _rootDirectory = Path.GetFullPath(options.ModelDirectory);
    }

    public Task<AssetFailureForecastModelArtifact?> LoadActiveAsync(
        CancellationToken cancellationToken = default) =>
        LoadFromManifestPathAsync(Path.Combine(_rootDirectory, "active.json"), cancellationToken);

    public Task<AssetFailureForecastModelArtifact?> LoadVersionAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Forecast model version is required.", nameof(version));
        return LoadFromManifestPathAsync(
            Path.Combine(_rootDirectory, $"manifest-{Safe(version)}.json"),
            cancellationToken);
    }

    public async Task<IReadOnlyList<AssetFailureForecastModelManifest>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootDirectory))
            return Array.Empty<AssetFailureForecastModelManifest>();
        var result = new List<AssetFailureForecastModelManifest>();
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "manifest-*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, cancellationToken);
            if (manifest is not null) result.Add(manifest);
        }
        return result
            .OrderByDescending(static item => item.CreatedUtc)
            .ThenByDescending(static item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task ActivateAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(
            Path.Combine(_rootDirectory, $"manifest-{Safe(version)}.json"),
            cancellationToken)
            ?? throw new KeyNotFoundException($"Failure forecast model was not found: {version}.");
        if (!string.Equals(manifest.Version, version, StringComparison.Ordinal))
            throw new InvalidOperationException("Forecast manifest Version does not match activation request.");
        await WriteManifestAtomicAsync(Path.Combine(_rootDirectory, "active.json"), manifest, cancellationToken);
    }

    private async Task<AssetFailureForecastModelArtifact?> LoadFromManifestPathAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        if (manifest is null) return null;

        var artifactPath = Path.GetFullPath(Path.Combine(_rootDirectory, manifest.ArtifactFile));
        if (!artifactPath.StartsWith(_rootDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(artifactPath))
            throw new InvalidOperationException("Forecast artifact is missing or outside the governed model directory.");

        var maximumBytes = Math.Clamp(_options.MaximumModelArtifactMegabytes, 1, 2_048) * 1024L * 1024L;
        var info = new FileInfo(artifactPath);
        if (info.Length <= 0 || info.Length > maximumBytes)
            throw new InvalidOperationException($"Forecast artifact size is invalid: {info.Length} bytes.");
        var content = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
        AssetFailureForecastManifestValidator.VerifyArtifactHash(manifest, content);
        return new AssetFailureForecastModelArtifact { Manifest = manifest, Content = content };
    }

    private static async Task<AssetFailureForecastModelManifest?> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<AssetFailureForecastModelManifest>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteManifestAtomicAsync(
        string path,
        AssetFailureForecastModelManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value.Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        return new string(characters);
    }
}

public sealed class OnnxAssetFailureForecastRuntime : IAssetFailureForecastRuntime
{
    public const string RuntimeAdapterId = "microsoft.onnxruntime.cpu.forecast.v1";
    private readonly InferenceSession _session;

    public OnnxAssetFailureForecastRuntime(
        AssetFailureForecastModelArtifact artifact,
        AssetFailureForecastOptions options)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);
        AssetFailureForecastManifestValidator.Validate(artifact.Manifest, options);
        AssetFailureForecastManifestValidator.VerifyArtifactHash(artifact.Manifest, artifact.Content.Span);
        Manifest = artifact.Manifest;
        using var sessionOptions = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            EnableMemoryPattern = true,
            EnableCpuMemArena = true
        };
        _session = new InferenceSession(artifact.Content.ToArray(), sessionOptions);
        ValidateSessionMetadata();
    }

    public AssetFailureForecastModelManifest Manifest { get; }

    public AssetFailureForecastOutput Predict(AssetFailureForecastFeatureVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (!Manifest.FeatureNames.SequenceEqual(vector.FeatureNames, StringComparer.Ordinal))
            throw new InvalidOperationException("Forecast feature order does not match the active model manifest.");
        if (vector.Values.Count != Manifest.FeatureNames.Length ||
            vector.Values.Any(static value => !double.IsFinite(value)))
            throw new InvalidOperationException("Forecast feature values are invalid.");

        var normalized = new float[vector.Values.Count];
        for (var index = 0; index < normalized.Length; index++)
            normalized[index] = checked((float)((vector.Values[index] - Manifest.Means[index]) / Manifest.StandardDeviations[index]));
        var tensor = new DenseTensor<float>(normalized, new[] { 1, normalized.Length });
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(Manifest.InputName, tensor) };
        using var results = _session.Run(inputs);
        var result = results.FirstOrDefault(item => string.Equals(item.Name, Manifest.OutputName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Forecast ONNX output was not found: {Manifest.OutputName}.");
        var values = result.AsEnumerable<float>().Select(static value => (double)value).ToArray();
        if (values.Length != 6)
            throw new InvalidOperationException($"Forecast ONNX output must contain six values, actual={values.Length}.");
        return AssetFailureForecastManifestValidator.ValidateOutput(
            new AssetFailureForecastOutput
            {
                FailureProbability24Hours = values[0],
                FailureProbability72Hours = values[1],
                FailureProbability168Hours = values[2],
                RulLowerHours = values[3],
                RulMedianHours = values[4],
                RulUpperHours = values[5]
            },
            Manifest.MaximumRulHours);
    }

    public void Dispose() => _session.Dispose();

    private void ValidateSessionMetadata()
    {
        if (!_session.InputMetadata.TryGetValue(Manifest.InputName, out var input))
            throw new InvalidOperationException($"Forecast ONNX input was not found: {Manifest.InputName}.");
        if (input.ElementType != typeof(float) || input.Dimensions.Length != 2)
            throw new InvalidOperationException("Forecast ONNX input must be a rank-2 float32 tensor.");
        if (input.Dimensions[0] is not (-1 or 1) || input.Dimensions[1] != Manifest.FeatureNames.Length)
            throw new InvalidOperationException("Forecast ONNX input dimensions do not match the manifest.");
        if (!_session.OutputMetadata.TryGetValue(Manifest.OutputName, out var output))
            throw new InvalidOperationException($"Forecast ONNX output was not found: {Manifest.OutputName}.");
        if (output.ElementType != typeof(float) || output.Dimensions.Length != 2)
            throw new InvalidOperationException("Forecast ONNX output must be a rank-2 float32 tensor.");
        if (output.Dimensions[0] is not (-1 or 1) || output.Dimensions[1] != 6)
            throw new InvalidOperationException("Forecast ONNX output dimensions must be [-1|1, 6].");
    }
}
