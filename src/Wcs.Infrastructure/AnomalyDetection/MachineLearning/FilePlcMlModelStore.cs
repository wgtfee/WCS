namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning;

using System.Text.Json;
using Wcs.Core.AnomalyDetection.MachineLearning;

public sealed class FilePlcMlModelStore : IPlcMlModelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly PlcMlAnomalyOptions _options;

    public FilePlcMlModelStore(PlcMlAnomalyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<PlcIsolationForestModel?> LoadActiveAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        ReadModelAsync(GetActivePath(profileId), cancellationToken);

    public async Task<PlcIsolationForestModel?> LoadVersionAsync(
        string profileId,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("模型版本不能为空。", nameof(version));
        var model = await ReadModelAsync(GetVersionPath(profileId, version), cancellationToken);
        if (model is null) return null;
        if (!string.Equals(model.ProfileId, profileId, StringComparison.Ordinal) ||
            !string.Equals(model.Version, version, StringComparison.Ordinal))
            throw new InvalidOperationException("模型文件元数据与请求的 Profile/Version 不一致。");
        return model;
    }

    public async Task SaveVersionAsync(
        PlcIsolationForestModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        Directory.CreateDirectory(GetProfileDirectory(model.ProfileId));
        await WriteAtomicAsync(GetVersionPath(model.ProfileId, model.Version), model, cancellationToken);
    }

    public async Task ActivateAsync(
        PlcIsolationForestModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        await SaveVersionAsync(model, cancellationToken);
        await WriteAtomicAsync(GetActivePath(model.ProfileId), model, cancellationToken);
    }

    public async Task SaveAndActivateAsync(
        PlcIsolationForestModel model,
        CancellationToken cancellationToken = default) =>
        await ActivateAsync(model, cancellationToken);

    public async Task<IReadOnlyList<PlcMlModelVersionInfo>> ListAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var directory = GetProfileDirectory(profileId);
        if (!Directory.Exists(directory)) return Array.Empty<PlcMlModelVersionInfo>();
        var active = await LoadActiveAsync(profileId, cancellationToken);
        var result = new List<PlcMlModelVersionInfo>();

        foreach (var path in Directory.EnumerateFiles(directory, "model-*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = await ReadModelAsync(path, cancellationToken);
            if (model is null || !string.Equals(model.ProfileId, profileId, StringComparison.Ordinal)) continue;
            result.Add(ToInfo(model, string.Equals(active?.Version, model.Version, StringComparison.Ordinal)));
        }

        return result
            .OrderByDescending(static item => item.CreatedUtc)
            .ThenByDescending(static item => item.Version, StringComparer.Ordinal)
            .ToList();
    }

    private static PlcMlModelVersionInfo ToInfo(PlcIsolationForestModel model, bool isActive) => new()
    {
        ProfileId = model.ProfileId,
        Version = model.Version,
        CreatedUtc = model.CreatedUtc,
        TrainingSampleCount = model.TrainingSampleCount,
        CalibrationSampleCount = model.CalibrationSampleCount,
        TreeCount = model.Trees.Length,
        DecisionThreshold = model.DecisionThreshold,
        IsActive = isActive
    };

    private static async Task<PlcIsolationForestModel?> ReadModelAsync(
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
        return await JsonSerializer.DeserializeAsync<PlcIsolationForestModel>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    private static async Task WriteAtomicAsync(
        string path,
        PlcIsolationForestModel model,
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
                await JsonSerializer.SerializeAsync(stream, model, JsonOptions, cancellationToken);
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

    private string GetActivePath(string profileId) =>
        Path.Combine(GetProfileDirectory(profileId), "active.json");

    private string GetVersionPath(string profileId, string version) =>
        Path.Combine(GetProfileDirectory(profileId), $"model-{Safe(version)}.json");

    private string GetProfileDirectory(string profileId) =>
        Path.Combine(Path.GetFullPath(_options.ModelDirectory), Safe(profileId));

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }
}
