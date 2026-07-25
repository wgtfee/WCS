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

    public async Task<PlcIsolationForestModel?> LoadActiveAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var path = GetActivePath(profileId);
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

    public async Task SaveAndActivateAsync(
        PlcIsolationForestModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var directory = GetProfileDirectory(model.ProfileId);
        Directory.CreateDirectory(directory);
        var versionPath = Path.Combine(directory, $"model-{Safe(model.Version)}.json");
        await WriteAtomicAsync(versionPath, model, cancellationToken);
        await WriteAtomicAsync(GetActivePath(model.ProfileId), model, cancellationToken);
    }

    private async Task WriteAtomicAsync(
        string path,
        PlcIsolationForestModel model,
        CancellationToken cancellationToken)
    {
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

    private string GetProfileDirectory(string profileId) =>
        Path.Combine(Path.GetFullPath(_options.ModelDirectory), Safe(profileId));

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }
}
