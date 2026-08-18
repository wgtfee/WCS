namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wcs.Core.AnomalyDetection.MachineLearning;

public sealed class FilePlcMlTrainingStore : IPlcMlTrainingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly PlcMlAnomalyOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public FilePlcMlTrainingStore(PlcMlAnomalyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<int> CountAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (_counts.TryGetValue(profileId, out var cached)) return cached;
        var gate = GetLock(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_counts.TryGetValue(profileId, out cached)) return cached;
            var count = await CountFileLinesUnsafeAsync(GetPath(profileId), cancellationToken);
            _counts[profileId] = count;
            return count;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AppendAsync(
        PlcFeatureVector vector,
        int maximumWindows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var gate = GetLock(vector.ProfileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var count = _counts.TryGetValue(vector.ProfileId, out var cached)
                ? cached
                : await CountFileLinesUnsafeAsync(GetPath(vector.ProfileId), cancellationToken);
            if (count >= maximumWindows)
            {
                _counts[vector.ProfileId] = count;
                return;
            }

            var path = GetPath(vector.ProfileId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(vector, JsonOptions) + Environment.NewLine);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            _counts[vector.ProfileId] = count + 1;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlcFeatureVector>> ReadAsync(
        string profileId,
        int maximumWindows,
        CancellationToken cancellationToken = default)
    {
        var gate = GetLock(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await ReadVectorsUnsafeAsync(GetPath(profileId), maximumWindows, cancellationToken);
            _counts[profileId] = result.Count;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlcMlDatasetInfo> CreateDatasetAsync(
        string profileId,
        int maximumWindows,
        string createdBy,
        string? description,
        CancellationToken cancellationToken = default)
    {
        var gate = GetLock(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var vectors = await ReadVectorsUnsafeAsync(GetPath(profileId), maximumWindows, cancellationToken);
            if (vectors.Count == 0)
                throw new InvalidOperationException($"Profile {profileId} 没有可冻结的训练窗口。");

            var createdUtc = DateTime.UtcNow;
            var version = $"{createdUtc:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..25];
            var featureHash = ComputeFeatureHash(vectors[0].FeatureNames);
            var info = new PlcMlDatasetInfo
            {
                ProfileId = profileId,
                Version = version,
                CreatedUtc = createdUtc,
                WindowCount = vectors.Count,
                FeatureHash = featureHash,
                CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? "system" : createdBy.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                IsFrozen = true
            };

            var dataPath = GetDatasetDataPath(profileId, version);
            var metadataPath = GetDatasetMetadataPath(profileId, version);
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            await WriteDatasetAtomicAsync(dataPath, vectors, cancellationToken);
            await WriteJsonAtomicAsync(metadataPath, info, cancellationToken);
            return info;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlcMlDatasetInfo>> ListDatasetsAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var directory = GetDatasetsDirectory(profileId);
        if (!Directory.Exists(directory)) return Array.Empty<PlcMlDatasetInfo>();
        var result = new List<PlcMlDatasetInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "dataset-*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(path);
            var info = await JsonSerializer.DeserializeAsync<PlcMlDatasetInfo>(stream, JsonOptions, cancellationToken);
            if (info is not null && string.Equals(info.ProfileId, profileId, StringComparison.Ordinal))
                result.Add(info);
        }

        return result
            .OrderByDescending(static item => item.CreatedUtc)
            .ThenByDescending(static item => item.Version, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<PlcFeatureVector>> ReadDatasetAsync(
        string profileId,
        string datasetVersion,
        int maximumWindows,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(datasetVersion))
            throw new ArgumentException("数据集版本不能为空。", nameof(datasetVersion));
        var path = GetDatasetDataPath(profileId, datasetVersion);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"未找到数据集：Profile={profileId}, Version={datasetVersion}。");
        return await ReadVectorsUnsafeAsync(path, maximumWindows, cancellationToken);
    }

    private async Task<IReadOnlyList<PlcFeatureVector>> ReadVectorsUnsafeAsync(
        string path,
        int maximumWindows,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Array.Empty<PlcFeatureVector>();
        var queue = new Queue<PlcFeatureVector>(Math.Min(Math.Max(1, maximumWindows), 1024));
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var vector = JsonSerializer.Deserialize<PlcFeatureVector>(line, JsonOptions);
            if (vector is null) continue;
            queue.Enqueue(vector);
            while (queue.Count > maximumWindows) queue.Dequeue();
        }
        return queue.ToArray();
    }

    private static async Task WriteDatasetAtomicAsync(
        string path,
        IReadOnlyList<PlcFeatureVector> vectors,
        CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
            {
                foreach (var vector in vectors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(JsonSerializer.Serialize(vector, JsonOptions));
                }
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<int> CountFileLinesUnsafeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return 0;
        var count = 0;
        await foreach (var _ in File.ReadLinesAsync(path, cancellationToken)) count++;
        return count;
    }

    private SemaphoreSlim GetLock(string profileId) =>
        _locks.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));

    private string GetPath(string profileId) =>
        Path.Combine(GetProfileDirectory(profileId), "features.jsonl");

    private string GetDatasetDataPath(string profileId, string version) =>
        Path.Combine(GetDatasetsDirectory(profileId), $"dataset-{Safe(version)}.jsonl");

    private string GetDatasetMetadataPath(string profileId, string version) =>
        Path.Combine(GetDatasetsDirectory(profileId), $"dataset-{Safe(version)}.json");

    private string GetDatasetsDirectory(string profileId) =>
        Path.Combine(GetProfileDirectory(profileId), "datasets");

    private string GetProfileDirectory(string profileId) =>
        Path.Combine(Path.GetFullPath(_options.TrainingDirectory), Safe(profileId));

    private static string ComputeFeatureHash(IEnumerable<string> featureNames)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", featureNames)));
        return Convert.ToHexString(bytes);
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
