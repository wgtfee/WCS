namespace Wcs.ModelOps;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wcs.IndustrialIntelligence.Governance;

public enum AiModelLifecycleStatus
{
    Draft = 0,
    Validated = 1,
    Candidate = 2,
    Shadow = 3,
    Champion = 4,
    Fallback = 5,
    Quarantined = 6,
    Retired = 7
}

public sealed record AiModelDefinition(
    string ModelId,
    string DisplayName,
    string AssetType,
    string Profile,
    string Description);

public sealed record AiModelRuntimeLimits(
    int MaximumInferenceMilliseconds,
    long MaximumWorkingSetBytes);

public sealed record AiModelPackageManifest(
    string ModelId,
    string ModelVersion,
    string ModelType,
    string ArtifactFile,
    string ArtifactSha256,
    string ManifestHash,
    string FeatureSchemaId,
    string FeatureSchemaHash,
    string TrainingDatasetVersion,
    string TrainingDatasetHash,
    int TrainingAssetCount,
    int FailureEventCount,
    Dictionary<string, double> ValidationMetrics,
    AiModelRuntimeLimits RuntimeLimits,
    string ApprovedBy,
    DateTimeOffset? ApprovedAtUtc,
    string? FallbackVersion,
    int[] InputShape,
    int[] OutputShape);

public sealed record AiModelVersion(
    AiModelPackageManifest Manifest,
    AiModelLifecycleStatus LifecycleStatus,
    DateTimeOffset RegisteredAtUtc,
    string RegisteredBy,
    string CorrelationId)
{
    public string ModelId => Manifest.ModelId;
    public string Version => Manifest.ModelVersion;
}

public sealed record AiModelDeployment(
    string ModelId,
    string ModelVersion,
    string AssetType,
    string Profile,
    AiModelLifecycleStatus Status,
    DateTimeOffset UpdatedAtUtc,
    string Actor,
    string Reason,
    string CorrelationId);

public sealed record AiModelEvaluation(
    string EvaluationId,
    string ModelId,
    string ModelVersion,
    string DatasetVersion,
    string DatasetHash,
    string MetricsJson,
    string EvidenceSha256,
    DateTimeOffset CreatedAtUtc,
    string CorrelationId);

public sealed record AiModelDriftEvent(
    string DriftEventId,
    string ModelId,
    string ModelVersion,
    string DriftKind,
    double ObservedValue,
    double Threshold,
    DateTimeOffset OccurredAtUtc,
    string EvidenceSha256,
    string CorrelationId);

public sealed record AiModelAuditEntry(
    string AuditId,
    string Action,
    string ModelId,
    string ModelVersion,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    string PayloadHash);

public sealed record ModelValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    AiModelPackageManifest? Manifest)
{
    public static ModelValidationResult Success(AiModelPackageManifest manifest) =>
        new(true, Array.Empty<string>(), manifest);

    public static ModelValidationResult Failure(params string[] errors) =>
        new(false, errors, null);
}

public sealed record ModelDeploymentRequest(
    string ModelId,
    string Version,
    string AssetType,
    string Profile,
    string Actor,
    string Reason,
    string CorrelationId);

public sealed record ModelRollbackRequest(
    string ModelId,
    string AssetType,
    string Profile,
    string Actor,
    string Reason,
    string CorrelationId);

public interface IModelRegistry
{
    Task RegisterAsync(AiModelVersion version, CancellationToken ct);
    Task<AiModelVersion?> GetAsync(string modelId, string version, CancellationToken ct);
    Task<IReadOnlyList<AiModelVersion>> ListAsync(string modelId, CancellationToken ct);
}

public interface IModelPackageValidator
{
    Task<ModelValidationResult> ValidateAsync(string packagePath, CancellationToken ct);
}

public interface IModelDeploymentManager
{
    Task PromoteToShadowAsync(ModelDeploymentRequest request, CancellationToken ct);
    Task PromoteToChampionAsync(ModelDeploymentRequest request, CancellationToken ct);
    Task RollbackAsync(ModelRollbackRequest request, CancellationToken ct);
}

public static class ModelManifestHash
{
    public static string Compute(AiModelPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var metrics = manifest.ValidationMetrics is null
            ? string.Empty
            : string.Join(",", manifest.ValidationMetrics
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}={x.Value.ToString("R", CultureInfo.InvariantCulture)}"));

        var canonical = string.Join("\n",
            manifest.ModelId?.Trim() ?? string.Empty,
            manifest.ModelVersion?.Trim() ?? string.Empty,
            manifest.ModelType?.Trim() ?? string.Empty,
            manifest.ArtifactFile?.Trim() ?? string.Empty,
            manifest.ArtifactSha256?.Trim().ToLowerInvariant() ?? string.Empty,
            manifest.FeatureSchemaId?.Trim() ?? string.Empty,
            manifest.FeatureSchemaHash?.Trim().ToLowerInvariant() ?? string.Empty,
            manifest.TrainingDatasetVersion?.Trim() ?? string.Empty,
            manifest.TrainingDatasetHash?.Trim().ToLowerInvariant() ?? string.Empty,
            manifest.TrainingAssetCount.ToString(CultureInfo.InvariantCulture),
            manifest.FailureEventCount.ToString(CultureInfo.InvariantCulture),
            metrics,
            manifest.RuntimeLimits?.MaximumInferenceMilliseconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            manifest.RuntimeLimits?.MaximumWorkingSetBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            manifest.ApprovedBy?.Trim() ?? string.Empty,
            manifest.ApprovedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            manifest.FallbackVersion?.Trim() ?? string.Empty,
            string.Join(",", manifest.InputShape ?? Array.Empty<int>()),
            string.Join(",", manifest.OutputShape ?? Array.Empty<int>()));

        return Hashing.Sha256(canonical);
    }
}

public static class ModelOpsContractRules
{
    public static IReadOnlyList<string> ValidateManifest(AiModelPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();

        Require(manifest.ModelId, nameof(manifest.ModelId), errors);
        Require(manifest.ModelVersion, nameof(manifest.ModelVersion), errors);
        Require(manifest.ModelType, nameof(manifest.ModelType), errors);
        Require(manifest.ArtifactFile, nameof(manifest.ArtifactFile), errors);
        Require(manifest.FeatureSchemaId, nameof(manifest.FeatureSchemaId), errors);
        Require(manifest.TrainingDatasetVersion, nameof(manifest.TrainingDatasetVersion), errors);

        if (!string.IsNullOrWhiteSpace(manifest.ArtifactFile))
        {
            if (!string.Equals(Path.GetFileName(manifest.ArtifactFile), manifest.ArtifactFile, StringComparison.Ordinal) ||
                manifest.ArtifactFile.Contains("..", StringComparison.Ordinal))
                errors.Add("ArtifactFile must be a package-root file name and must not traverse directories.");
            if (!string.Equals(Path.GetExtension(manifest.ArtifactFile), ".onnx", StringComparison.OrdinalIgnoreCase))
                errors.Add("ArtifactFile must use the .onnx extension.");
        }

        ValidateHash(manifest.ArtifactSha256, nameof(manifest.ArtifactSha256), errors);
        ValidateHash(manifest.FeatureSchemaHash, nameof(manifest.FeatureSchemaHash), errors);
        ValidateHash(manifest.TrainingDatasetHash, nameof(manifest.TrainingDatasetHash), errors);
        ValidateHash(manifest.ManifestHash, nameof(manifest.ManifestHash), errors);

        if (Hashing.IsSha256(manifest.ManifestHash))
        {
            var computed = ModelManifestHash.Compute(manifest);
            if (!string.Equals(computed, manifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
                errors.Add("ManifestHash does not match the canonical manifest payload.");
        }

        if (manifest.TrainingAssetCount < 0)
            errors.Add("TrainingAssetCount cannot be negative.");
        if (manifest.FailureEventCount < 0)
            errors.Add("FailureEventCount cannot be negative.");

        if (manifest.ValidationMetrics is null || manifest.ValidationMetrics.Count == 0)
            errors.Add("ValidationMetrics are required.");
        else if (manifest.ValidationMetrics.Any(x => string.IsNullOrWhiteSpace(x.Key) || !double.IsFinite(x.Value)))
            errors.Add("ValidationMetrics must have non-empty names and finite values.");

        if (manifest.RuntimeLimits is null)
        {
            errors.Add("RuntimeLimits are required.");
        }
        else
        {
            if (manifest.RuntimeLimits.MaximumInferenceMilliseconds is < 10 or > 60_000)
                errors.Add("MaximumInferenceMilliseconds must be in [10,60000].");
            if (manifest.RuntimeLimits.MaximumWorkingSetBytes is < 1_048_576 or > 1_073_741_824)
                errors.Add("MaximumWorkingSetBytes must be in [1MiB,1GiB].");
        }

        ValidateShape(manifest.InputShape, "InputShape", errors);
        ValidateShape(manifest.OutputShape, "OutputShape", errors);

        if (string.IsNullOrWhiteSpace(manifest.ApprovedBy) != !manifest.ApprovedAtUtc.HasValue)
            errors.Add("ApprovedBy and ApprovedAtUtc must either both be supplied or both be empty.");

        return errors;
    }

    public static bool IsApproved(AiModelPackageManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.ApprovedBy) && manifest.ApprovedAtUtc.HasValue;

    private static void Require(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{name} is required.");
    }

    private static void ValidateHash(string? value, string name, ICollection<string> errors)
    {
        if (!Hashing.IsSha256(value))
            errors.Add($"{name} must be a SHA-256 value.");
    }

    private static void ValidateShape(int[]? shape, string name, ICollection<string> errors)
    {
        if (shape is null || shape.Length == 0 || shape.Any(x => x <= 0))
            errors.Add($"{name} must contain positive dimensions.");
    }
}

public sealed class InMemoryModelRegistry : IModelRegistry
{
    private readonly ConcurrentDictionary<string, AiModelVersion> _versions =
        new(StringComparer.OrdinalIgnoreCase);

    public Task RegisterAsync(AiModelVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        ct.ThrowIfCancellationRequested();

        var errors = ModelOpsContractRules.ValidateManifest(version.Manifest);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(version));
        _ = ActorReason.Create(version.RegisteredBy, "model registration");
        if (string.IsNullOrWhiteSpace(version.CorrelationId))
            throw new ArgumentException("CorrelationId is required.", nameof(version));

        var key = Key(version.ModelId, version.Version);
        while (true)
        {
            if (_versions.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.Manifest.ManifestHash, version.Manifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Model '{version.ModelId}' version '{version.Version}' already exists with a different ManifestHash.");
                return Task.CompletedTask;
            }

            if (_versions.TryAdd(key, version))
                return Task.CompletedTask;
        }
    }

    public Task<AiModelVersion?> GetAsync(string modelId, string version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _versions.TryGetValue(Key(modelId, version), out var value);
        return Task.FromResult(value);
    }

    public Task<IReadOnlyList<AiModelVersion>> ListAsync(string modelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var values = _versions.Values
            .Where(x => string.Equals(x.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Version, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AiModelVersion>>(values);
    }

    private static string Key(string modelId, string version)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("ModelId is required.", nameof(modelId));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version is required.", nameof(version));
        return $"{modelId.Trim()}\u001f{version.Trim()}";
    }
}

public sealed class LocalModelPackageValidator : IModelPackageValidator
{
    private static readonly string[] RequiredFiles =
    [
        "manifest.json",
        "feature-schema.json",
        "normalization.json",
        "validation-evidence.json"
    ];

    private readonly long _maximumPackageBytes;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LocalModelPackageValidator(long maximumPackageBytes)
    {
        if (maximumPackageBytes is < 1_048_576 or > 1_073_741_824)
            throw new ArgumentOutOfRangeException(nameof(maximumPackageBytes));
        _maximumPackageBytes = maximumPackageBytes;
    }

    public async Task<ModelValidationResult> ValidateAsync(string packagePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            return ModelValidationResult.Failure("Package path is required.");

        string root;
        try
        {
            root = Path.GetFullPath(packagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ModelValidationResult.Failure("Package path is invalid.");
        }

        if (!Directory.Exists(root))
            return ModelValidationResult.Failure("Package directory does not exist.");

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        long totalBytes = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return ModelValidationResult.Failure("Model package must not contain symbolic links or reparse points.");
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > _maximumPackageBytes)
                return ModelValidationResult.Failure("Model package exceeds MaximumModelPackageBytes.");
        }

        foreach (var required in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(root, required)))
                return ModelValidationResult.Failure($"Required package file '{required}' is missing.");
        }

        AiModelPackageManifest? manifest;
        try
        {
            var json = await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), ct);
            manifest = JsonSerializer.Deserialize<AiModelPackageManifest>(json, _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ModelValidationResult.Failure("manifest.json cannot be read or parsed.");
        }

        if (manifest is null)
            return ModelValidationResult.Failure("manifest.json is empty.");

        var errors = ModelOpsContractRules.ValidateManifest(manifest).ToList();
        if (errors.Count > 0)
            return new ModelValidationResult(false, errors, manifest);

        var artifactPath = ResolveContainedFile(root, manifest.ArtifactFile);
        if (artifactPath is null)
            return new ModelValidationResult(false, ["Artifact path escapes the package root."], manifest);
        if (!File.Exists(artifactPath))
            return new ModelValidationResult(false, ["Model artifact file is missing."], manifest);

        var actualArtifactHash = await ComputeFileSha256Async(artifactPath, ct);
        if (!string.Equals(actualArtifactHash, manifest.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            errors.Add("ArtifactSha256 does not match the model artifact.");

        var featureSchemaPath = Path.Combine(root, "feature-schema.json");
        var actualFeatureSchemaHash = await ComputeFileSha256Async(featureSchemaPath, ct);
        if (!string.Equals(actualFeatureSchemaHash, manifest.FeatureSchemaHash, StringComparison.OrdinalIgnoreCase))
            errors.Add("FeatureSchemaHash does not match feature-schema.json.");

        return errors.Count == 0
            ? ModelValidationResult.Success(manifest)
            : new ModelValidationResult(false, errors, manifest);
    }

    private static string? ResolveContainedFile(string root, string relativeFile)
    {
        try
        {
            var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(root, relativeFile));
            return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class InMemoryModelDeploymentManager : IModelDeploymentManager
{
    private readonly IModelRegistry _registry;
    private readonly object _sync = new();
    private readonly Dictionary<string, AiModelDeployment> _deployments =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemoryModelDeploymentManager(IModelRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task PromoteToShadowAsync(ModelDeploymentRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        var version = await RequireVersionAsync(request.ModelId, request.Version, ct);
        if (!ModelOpsContractRules.IsApproved(version.Manifest))
            throw new InvalidOperationException("Only an approved model version may enter Shadow.");
        if (version.LifecycleStatus is not (AiModelLifecycleStatus.Candidate or AiModelLifecycleStatus.Shadow))
            throw new InvalidOperationException("Only Candidate model versions may enter Shadow.");

        lock (_sync)
        {
            _deployments[DeploymentKey(request.ModelId, request.Version, request.AssetType, request.Profile)] =
                NewDeployment(request, AiModelLifecycleStatus.Shadow);
        }
    }

    public async Task PromoteToChampionAsync(ModelDeploymentRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        _ = await RequireVersionAsync(request.ModelId, request.Version, ct);

        lock (_sync)
        {
            var key = DeploymentKey(request.ModelId, request.Version, request.AssetType, request.Profile);
            if (!_deployments.TryGetValue(key, out var candidate) || candidate.Status != AiModelLifecycleStatus.Shadow)
                throw new InvalidOperationException("A model must be in Shadow before Champion promotion.");

            var currentChampion = _deployments.Values.FirstOrDefault(x =>
                ScopeMatches(x, request) && x.Status == AiModelLifecycleStatus.Champion);
            if (currentChampion is not null && !string.Equals(currentChampion.ModelVersion, request.Version, StringComparison.OrdinalIgnoreCase))
            {
                _deployments[DeploymentKey(currentChampion.ModelId, currentChampion.ModelVersion, currentChampion.AssetType, currentChampion.Profile)] =
                    currentChampion with
                    {
                        Status = AiModelLifecycleStatus.Fallback,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Actor = request.Actor,
                        Reason = $"fallback after champion promotion: {request.Reason}",
                        CorrelationId = request.CorrelationId
                    };
            }

            _deployments[key] = candidate with
            {
                Status = AiModelLifecycleStatus.Champion,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Actor = request.Actor,
                Reason = request.Reason,
                CorrelationId = request.CorrelationId
            };
        }
    }

    public async Task RollbackAsync(ModelRollbackRequest request, CancellationToken ct)
    {
        ValidateRollbackRequest(request);

        AiModelDeployment? fallback;
        AiModelDeployment? champion;
        lock (_sync)
        {
            fallback = _deployments.Values.SingleOrDefault(x =>
                ScopeMatches(x, request.ModelId, request.AssetType, request.Profile) &&
                x.Status == AiModelLifecycleStatus.Fallback);
            champion = _deployments.Values.SingleOrDefault(x =>
                ScopeMatches(x, request.ModelId, request.AssetType, request.Profile) &&
                x.Status == AiModelLifecycleStatus.Champion);
        }

        if (fallback is null)
            throw new InvalidOperationException("No valid Fallback deployment exists for this scope.");

        var fallbackVersion = await RequireVersionAsync(fallback.ModelId, fallback.ModelVersion, ct);
        if (fallbackVersion.LifecycleStatus is AiModelLifecycleStatus.Quarantined or AiModelLifecycleStatus.Retired)
            throw new InvalidOperationException("Fallback version is not eligible for rollback.");

        lock (_sync)
        {
            var fallbackKey = DeploymentKey(fallback.ModelId, fallback.ModelVersion, fallback.AssetType, fallback.Profile);
            _deployments[fallbackKey] = fallback with
            {
                Status = AiModelLifecycleStatus.Champion,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Actor = request.Actor,
                Reason = request.Reason,
                CorrelationId = request.CorrelationId
            };

            if (champion is not null)
            {
                var championKey = DeploymentKey(champion.ModelId, champion.ModelVersion, champion.AssetType, champion.Profile);
                _deployments[championKey] = champion with
                {
                    Status = AiModelLifecycleStatus.Fallback,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Actor = request.Actor,
                    Reason = $"fallback after rollback: {request.Reason}",
                    CorrelationId = request.CorrelationId
                };
            }
        }
    }

    public IReadOnlyList<AiModelDeployment> Snapshot()
    {
        lock (_sync)
        {
            return _deployments.Values
                .OrderBy(x => x.ModelId, StringComparer.Ordinal)
                .ThenBy(x => x.AssetType, StringComparer.Ordinal)
                .ThenBy(x => x.Profile, StringComparer.Ordinal)
                .ThenBy(x => x.ModelVersion, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private async Task<AiModelVersion> RequireVersionAsync(string modelId, string version, CancellationToken ct) =>
        await _registry.GetAsync(modelId, version, ct)
        ?? throw new InvalidOperationException($"Model '{modelId}' version '{version}' is not registered.");

    private static AiModelDeployment NewDeployment(ModelDeploymentRequest request, AiModelLifecycleStatus status) =>
        new(
            request.ModelId.Trim(),
            request.Version.Trim(),
            request.AssetType.Trim(),
            request.Profile.Trim(),
            status,
            DateTimeOffset.UtcNow,
            request.Actor.Trim(),
            request.Reason.Trim(),
            request.CorrelationId.Trim());

    private static void ValidateRequest(ModelDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.ModelId, nameof(request.ModelId));
        Require(request.Version, nameof(request.Version));
        Require(request.AssetType, nameof(request.AssetType));
        Require(request.Profile, nameof(request.Profile));
        Require(request.CorrelationId, nameof(request.CorrelationId));
        _ = ActorReason.Create(request.Actor, request.Reason);
    }

    private static void ValidateRollbackRequest(ModelRollbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.ModelId, nameof(request.ModelId));
        Require(request.AssetType, nameof(request.AssetType));
        Require(request.Profile, nameof(request.Profile));
        Require(request.CorrelationId, nameof(request.CorrelationId));
        _ = ActorReason.Create(request.Actor, request.Reason);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
    }

    private static string DeploymentKey(string modelId, string version, string assetType, string profile) =>
        $"{modelId.Trim()}\u001f{version.Trim()}\u001f{assetType.Trim()}\u001f{profile.Trim()}";

    private static bool ScopeMatches(AiModelDeployment deployment, ModelDeploymentRequest request) =>
        ScopeMatches(deployment, request.ModelId, request.AssetType, request.Profile);

    private static bool ScopeMatches(AiModelDeployment deployment, string modelId, string assetType, string profile) =>
        string.Equals(deployment.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(deployment.AssetType, assetType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(deployment.Profile, profile, StringComparison.OrdinalIgnoreCase);
}
