namespace Wcs.Infrastructure.Telemetry;

using System.Text.Json;
using Wcs.Core.Telemetry;

internal sealed record PlcTelemetrySpoolBatch(
    string FilePath,
    IReadOnlyList<PlcTelemetryPoint> Points);

/// <summary>
/// PLC telemetry 的本地持久化队列。
/// Buffered 模式用于数据库故障兜底；WriteAhead 模式用于先落盘再确认接收。
/// 每个批次先写临时文件、强制刷盘，再原子重命名为可消费文件。
/// </summary>
internal sealed class FilePlcTelemetrySpool
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private long _pendingPoints;

    public FilePlcTelemetrySpool(PlcTelemetryOptions options)
    {
        _directory = Path.GetFullPath(options.SpoolDirectory);
        Directory.CreateDirectory(_directory);
        RecoverCompletedTemporaryFiles();
        _pendingPoints = CountExistingPoints();
    }

    public long PendingPoints => Interlocked.Read(ref _pendingPoints);

    public async Task AppendAsync(
        IReadOnlyCollection<PlcTelemetryPoint> points,
        CancellationToken cancellationToken)
    {
        if (points.Count == 0) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json";
            var finalPath = Path.Combine(_directory, name);
            var tempPath = finalPath + ".tmp";

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, points, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, finalPath);
            Interlocked.Add(ref _pendingPoints, points.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlcTelemetrySpoolBatch?> TryPeekOldestAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var file in Directory
                         .EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => path, StringComparer.Ordinal))
            {
                try
                {
                    await using var stream = new FileStream(
                        file,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var points = await JsonSerializer.DeserializeAsync<List<PlcTelemetryPoint>>(
                        stream,
                        _jsonOptions,
                        cancellationToken) ?? new List<PlcTelemetryPoint>();

                    return new PlcTelemetrySpoolBatch(file, points);
                }
                catch (JsonException)
                {
                    Quarantine(file);
                    Interlocked.Exchange(ref _pendingPoints, CountExistingPoints());
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeAsync(
        PlcTelemetrySpoolBatch batch,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(batch.FilePath))
            {
                File.Delete(batch.FilePath);
                Interlocked.Add(ref _pendingPoints, -batch.Points.Count);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RecoverCompletedTemporaryFiles()
    {
        foreach (var tempPath in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            try
            {
                using (var stream = File.OpenRead(tempPath))
                {
                    _ = JsonSerializer.Deserialize<List<PlcTelemetryPoint>>(stream, _jsonOptions)
                        ?? throw new JsonException("Empty telemetry WAL batch.");
                }

                var finalPath = tempPath[..^4];
                if (File.Exists(finalPath))
                    File.Delete(tempPath);
                else
                    File.Move(tempPath, finalPath);
            }
            catch
            {
                Quarantine(tempPath);
            }
        }
    }

    private long CountExistingPoints()
    {
        long count = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var points = JsonSerializer.Deserialize<List<PlcTelemetryPoint>>(stream, _jsonOptions);
                count += points?.Count ?? 0;
            }
            catch
            {
                // 损坏文件保留给运维处理，不静默删除。
            }
        }
        return count;
    }

    private static void Quarantine(string file)
    {
        if (!File.Exists(file)) return;
        var quarantine = file + $".{DateTime.UtcNow:yyyyMMddHHmmssfffffff}.corrupt";
        File.Move(file, quarantine, overwrite: false);
    }
}
