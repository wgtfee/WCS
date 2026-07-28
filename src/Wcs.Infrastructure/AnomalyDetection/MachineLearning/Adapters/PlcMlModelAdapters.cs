namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning.Adapters;

using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Wcs.Core.AnomalyDetection.MachineLearning;
using Wcs.Core.AnomalyDetection.MachineLearning.Adapters;

public sealed class FilePlcMlExternalModelStore : IPlcMlExternalModelStore
{
    private const int MaximumArtifactBytes = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _rootDirectory;

    public FilePlcMlExternalModelStore(PlcMlAnomalyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootDirectory = Path.GetFullPath(Path.Combine(options.ModelDirectory, "external"));
    }

    public Task<PlcMlModelArtifact?> LoadActiveAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        LoadFromManifestPathAsync(profileId, GetActiveManifestPath(profileId), cancellationToken);

    public Task<PlcMlModelArtifact?> LoadVersionAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Model version is required.", nameof(version));
        return LoadFromManifestPathAsync(profileId, GetVersionManifestPath(profileId, version), cancellationToken);
    }

    public async Task<IReadOnlyList<PlcMlModelManifest>> ListAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var profileDirectory = GetProfileDirectory(profileId);
        if (!Directory.Exists(profileDirectory)) return Array.Empty<PlcMlModelManifest>();
        var result = new List<PlcMlModelManifest>();
        foreach (var path in Directory.EnumerateFiles(directory: profileDirectory, searchPattern: "manifest-*.json", searchOption: SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadManifestAsync(path, cancellationToken);
            if (manifest is null || !string.Equals(manifest.ProfileId, profileId, StringComparison.Ordinal)) continue;
            result.Add(manifest);
        }
        return result
            .OrderByDescending(static item => item.CreatedUtc)
            .ThenByDescending(static item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task ActivateAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(GetVersionManifestPath(profileId, version), cancellationToken)
            ?? throw new KeyNotFoundException($"External PLC ML model not found: {profileId}/{version}.");
        if (!string.Equals(manifest.ProfileId, profileId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, version, StringComparison.Ordinal))
            throw new InvalidOperationException("External model manifest metadata does not match the activation request.");
        await WriteManifestAtomicAsync(GetActiveManifestPath(profileId), manifest, cancellationToken);
    }

    private async Task<PlcMlModelArtifact?> LoadFromManifestPathAsync(
        string profileId,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        if (manifest is null) return null;
        if (!string.Equals(manifest.ProfileId, profileId, StringComparison.Ordinal))
            throw new InvalidOperationException("External model manifest ProfileId does not match its directory.");

        var profileDirectory = GetProfileDirectory(profileId);
        var artifactPath = Path.GetFullPath(Path.Combine(profileDirectory, manifest.ArtifactFile));
        if (!artifactPath.StartsWith(profileDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(artifactPath))
            throw new InvalidOperationException("External model artifact is missing or outside the profile directory.");

        var info = new FileInfo(artifactPath);
        if (info.Length <= 0 || info.Length > MaximumArtifactBytes)
            throw new InvalidOperationException($"External model artifact size is invalid: {info.Length} bytes.");
        var content = await File.ReadAllBytesAsync(artifactPath, cancellationToken);
        PlcMlModelManifestValidator.VerifyArtifactHash(manifest, content);
        return new PlcMlModelArtifact { Manifest = manifest, Content = content };
    }

    private static async Task<PlcMlModelManifest?> ReadManifestAsync(
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
        return await JsonSerializer.DeserializeAsync<PlcMlModelManifest>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteManifestAtomicAsync(
        string path,
        PlcMlModelManifest manifest,
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

    private string GetActiveManifestPath(string profileId) =>
        Path.Combine(GetProfileDirectory(profileId), "active.json");

    private string GetVersionManifestPath(string profileId, string version) =>
        Path.Combine(GetProfileDirectory(profileId), $"manifest-{Safe(version)}.json");

    private string GetProfileDirectory(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("ProfileId is required.", nameof(profileId));
        var directory = Path.GetFullPath(Path.Combine(_rootDirectory, Safe(profileId)));
        if (!directory.StartsWith(_rootDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid external model profile directory.");
        return directory;
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }
}

public sealed class IsolationForestPlcMlModelAdapter : IPlcMlModelAdapter
{
    public const string RuntimeAdapterId = "wcs.isolation-forest.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PlcMlModelAdapterKind Kind => PlcMlModelAdapterKind.IsolationForest;
    public string AdapterId => RuntimeAdapterId;

    public Task<IPlcMlModelRuntime> LoadAsync(
        PlcMlProfile profile,
        PlcMlModelArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlcMlModelManifestValidator.Validate(profile, artifact.Manifest);
        if (artifact.Manifest.AdapterKind != Kind ||
            !string.Equals(artifact.Manifest.AdapterId, AdapterId, StringComparison.Ordinal))
            throw new InvalidOperationException("Isolation Forest manifest adapter metadata is invalid.");
        PlcMlModelManifestValidator.VerifyArtifactHash(artifact.Manifest, artifact.Content.Span);
        var model = JsonSerializer.Deserialize<PlcIsolationForestModel>(artifact.Content.Span, JsonOptions)
            ?? throw new InvalidOperationException("Isolation Forest artifact cannot be deserialized.");
        if (!string.Equals(model.ProfileId, artifact.Manifest.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(model.Version, artifact.Manifest.Version, StringComparison.Ordinal) ||
            !model.FeatureNames.SequenceEqual(artifact.Manifest.FeatureNames, StringComparer.Ordinal))
            throw new InvalidOperationException("Isolation Forest artifact metadata does not match the manifest.");
        if (model.Means.Length != model.FeatureNames.Length ||
            model.StandardDeviations.Length != model.FeatureNames.Length ||
            model.Trees.Length == 0)
            throw new InvalidOperationException("Isolation Forest artifact is incomplete.");
        return Task.FromResult<IPlcMlModelRuntime>(new IsolationForestRuntime(artifact.Manifest, model));
    }

    private sealed class IsolationForestRuntime : IPlcMlModelRuntime
    {
        private readonly PlcIsolationForestModel _model;

        public IsolationForestRuntime(PlcMlModelManifest manifest, PlcIsolationForestModel model)
        {
            Manifest = manifest;
            _model = model;
        }

        public PlcMlModelManifest Manifest { get; }

        public PlcMlAdapterPrediction Predict(PlcFeatureVector vector)
        {
            EnsureVector(Manifest, vector);
            var score = IsolationForest.Score(_model, vector.Values);
            var normalized = IsolationForest.Normalize(vector.Values, _model.Means, _model.StandardDeviations);
            var important = normalized
                .Select((value, index) => new { Name = Manifest.FeatureNames[index], Z = Math.Abs(value), Raw = vector.Values[index] })
                .OrderByDescending(static item => item.Z)
                .Take(3)
                .Select(static item => $"{item.Name}={item.Raw:G6}(deviation {item.Z:F2} sigma)");
            return new PlcMlAdapterPrediction
            {
                ProfileId = Manifest.ProfileId,
                ModelVersion = Manifest.Version,
                AdapterKind = PlcMlModelAdapterKind.IsolationForest,
                AdapterId = RuntimeAdapterId,
                DetectorName = "IsolationForest",
                Score = score,
                DecisionThreshold = Manifest.DecisionThreshold,
                IsAnomaly = score >= Manifest.DecisionThreshold,
                Explanation = $"Isolation Forest score {score:F4}, threshold {Manifest.DecisionThreshold:F4}; {string.Join(", ", important)}",
                CalibrationMeanScore = Manifest.CalibrationMeanScore,
                CalibrationP95Score = Manifest.CalibrationP95Score
            };
        }

        public void Dispose()
        {
        }
    }

    internal static void EnsureVector(PlcMlModelManifest manifest, PlcFeatureVector vector)
    {
        if (!string.Equals(manifest.ProfileId, vector.ProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("Feature vector ProfileId does not match model manifest.");
        if (!manifest.FeatureNames.SequenceEqual(vector.FeatureNames, StringComparer.Ordinal))
            throw new InvalidOperationException("Feature vector order does not match model manifest.");
        if (vector.Values.Length != manifest.FeatureNames.Length || vector.Values.Any(static value => !double.IsFinite(value)))
            throw new InvalidOperationException("Feature vector values are invalid.");
    }
}

public sealed class OnnxPlcMlModelAdapter : IPlcMlModelAdapter
{
    public const string RuntimeAdapterId = "microsoft.onnxruntime.cpu.v1";

    public PlcMlModelAdapterKind Kind => PlcMlModelAdapterKind.Onnx;
    public string AdapterId => RuntimeAdapterId;

    public Task<IPlcMlModelRuntime> LoadAsync(
        PlcMlProfile profile,
        PlcMlModelArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlcMlModelManifestValidator.Validate(profile, artifact.Manifest);
        if (artifact.Manifest.AdapterKind != Kind ||
            !string.Equals(artifact.Manifest.AdapterId, AdapterId, StringComparison.Ordinal))
            throw new InvalidOperationException("ONNX manifest adapter metadata is invalid.");
        PlcMlModelManifestValidator.VerifyArtifactHash(artifact.Manifest, artifact.Content.Span);
        return Task.FromResult<IPlcMlModelRuntime>(new OnnxRuntime(artifact.Manifest, artifact.Content.ToArray()));
    }

    private sealed class OnnxRuntime : IPlcMlModelRuntime
    {
        private readonly InferenceSession _session;

        public OnnxRuntime(PlcMlModelManifest manifest, byte[] modelBytes)
        {
            Manifest = manifest;
            using var options = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = 1,
                EnableMemoryPattern = true,
                EnableCpuMemArena = true
            };
            _session = new InferenceSession(modelBytes, options);
            ValidateSessionMetadata();
        }

        public PlcMlModelManifest Manifest { get; }

        public PlcMlAdapterPrediction Predict(PlcFeatureVector vector)
        {
            IsolationForestPlcMlModelAdapter.EnsureVector(Manifest, vector);
            var normalized = new float[vector.Values.Length];
            for (var index = 0; index < normalized.Length; index++)
                normalized[index] = checked((float)((vector.Values[index] - Manifest.Means[index]) / Manifest.StandardDeviations[index]));

            var tensor = new DenseTensor<float>(normalized, new[] { 1, normalized.Length });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(Manifest.InputName, tensor) };
            using var results = _session.Run(inputs);
            var output = results.FirstOrDefault(result => string.Equals(result.Name, Manifest.OutputName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"ONNX output not found: {Manifest.OutputName}.");
            var rawScore = output.AsEnumerable<float>().FirstOrDefault();
            var score = PlcMlScoreTransformer.Apply(rawScore, Manifest.ScoreTransform);
            var important = normalized
                .Select((value, index) => new { Name = Manifest.FeatureNames[index], Z = Math.Abs(value), Raw = vector.Values[index] })
                .OrderByDescending(static item => item.Z)
                .Take(3)
                .Select(static item => $"{item.Name}={item.Raw:G6}(deviation {item.Z:F2} sigma)");
            return new PlcMlAdapterPrediction
            {
                ProfileId = Manifest.ProfileId,
                ModelVersion = Manifest.Version,
                AdapterKind = PlcMlModelAdapterKind.Onnx,
                AdapterId = RuntimeAdapterId,
                DetectorName = "ONNXRuntime",
                Score = score,
                DecisionThreshold = Manifest.DecisionThreshold,
                IsAnomaly = score >= Manifest.DecisionThreshold,
                Explanation = $"ONNX score {score:F4}, threshold {Manifest.DecisionThreshold:F4}; {string.Join(", ", important)}",
                CalibrationMeanScore = Manifest.CalibrationMeanScore,
                CalibrationP95Score = Manifest.CalibrationP95Score
            };
        }

        public void Dispose() => _session.Dispose();

        private void ValidateSessionMetadata()
        {
            if (!_session.InputMetadata.TryGetValue(Manifest.InputName, out var input))
                throw new InvalidOperationException($"ONNX input not found: {Manifest.InputName}.");
            if (!_session.OutputMetadata.TryGetValue(Manifest.OutputName, out var output))
                throw new InvalidOperationException($"ONNX output not found: {Manifest.OutputName}.");
            if (input.ElementType != typeof(float) || output.ElementType != typeof(float))
                throw new InvalidOperationException("ONNX input and output tensors must use float32.");
            var dimensions = input.Dimensions;
            if (dimensions.Length != 2 ||
                dimensions[0] is not (-1 or 1) ||
                dimensions[1] != Manifest.FeatureNames.Length)
                throw new InvalidOperationException(
                    $"ONNX input shape must be [-1|1,{Manifest.FeatureNames.Length}], actual=[{string.Join(',', dimensions)}].");
        }
    }
}
