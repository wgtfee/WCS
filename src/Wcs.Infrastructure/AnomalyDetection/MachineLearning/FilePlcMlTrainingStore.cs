namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Wcs.Core.AnomalyDetection.MachineLearning;

public sealed class FilePlcMlTrainingStore : IPlcMlTrainingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
        var gate = _locks.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_counts.TryGetValue(profileId, out cached)) return cached;
            var path = GetPath(profileId);
            var count = 0;
            if (File.Exists(path))
            {
                await foreach (var _ in File.ReadLinesAsync(path, cancellationToken)) count++;
            }
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
        var gate = _locks.GetOrAdd(vector.ProfileId, static _ => new SemaphoreSlim(1, 1));
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
            var json = JsonSerializer.Serialize(vector, JsonOptions) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(json);
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
        var gate = _locks.GetOrAdd(profileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetPath(profileId);
            if (!File.Exists(path)) return Array.Empty<PlcFeatureVector>();
            var queue = new Queue<PlcFeatureVector>(Math.Min(maximumWindows, 1024));
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var vector = JsonSerializer.Deserialize<PlcFeatureVector>(line, JsonOptions);
                if (vector is null) continue;
                queue.Enqueue(vector);
                while (queue.Count > maximumWindows) queue.Dequeue();
            }
            _counts[profileId] = queue.Count;
            return queue.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> CountFileLinesUnsafeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return 0;
        var count = 0;
        await foreach (var _ in File.ReadLinesAsync(path, cancellationToken)) count++;
        return count;
    }

    private string GetPath(string profileId) =>
        Path.Combine(Path.GetFullPath(_options.TrainingDirectory), Safe(profileId), "features.jsonl");

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
