namespace Wcs.Infrastructure.Persistence.Services;

using System.Text.Json;
using Wcs.Core.TransportScheduling;

public sealed class FileTransportLogicalBackupStorage : ITransportLogicalBackupStorage
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FileTransportLogicalBackupStorage(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("备份目录不能为空", nameof(directory));
        _directory = Path.GetFullPath(Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(AppContext.BaseDirectory, directory));
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(
        TransportLogicalBackupManifest manifest,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var payloadPath = ResolvePayloadPath(manifest.FileName);
            var manifestPath = ResolveManifestPath(manifest.BackupId);
            var payloadTemp = payloadPath + ".tmp";
            var manifestTemp = manifestPath + ".tmp";
            await File.WriteAllBytesAsync(payloadTemp, payload, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                manifestTemp,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(payloadTemp, payloadPath, overwrite: true);
            File.Move(manifestTemp, manifestPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TransportLogicalBackupContent?> LoadAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupId))
            return null;
        var manifestPath = ResolveManifestPath(backupId);
        if (!File.Exists(manifestPath))
            return null;
        var manifest = JsonSerializer.Deserialize<TransportLogicalBackupManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            JsonOptions);
        if (manifest is null)
            return null;
        var payloadPath = ResolvePayloadPath(manifest.FileName);
        if (!File.Exists(payloadPath))
            return null;
        return new TransportLogicalBackupContent
        {
            Manifest = manifest,
            Payload = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task<IReadOnlyList<TransportLogicalBackupManifest>> GetManifestsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var result = new List<TransportLogicalBackupManifest>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.manifest.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = JsonSerializer.Deserialize<TransportLogicalBackupManifest>(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
                if (manifest is not null)
                    result.Add(manifest);
            }
            catch (JsonException)
            {
                // 损坏的清单由备份校验接口暴露，不阻断其他备份列表读取。
            }
        }
        return result
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(maxCount, 1, 1000))
            .ToArray();
    }

    public async Task<int> TrimAsync(
        int retentionCount,
        CancellationToken cancellationToken = default)
    {
        retentionCount = Math.Max(1, retentionCount);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifests = await GetManifestsAsync(1000, cancellationToken).ConfigureAwait(false);
            var removed = 0;
            foreach (var manifest in manifests.Skip(retentionCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(ResolvePayloadPath(manifest.FileName));
                TryDelete(ResolveManifestPath(manifest.BackupId));
                removed++;
            }
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveManifestPath(string backupId)
    {
        if (string.IsNullOrWhiteSpace(backupId) || backupId.Any(x => !char.IsLetterOrDigit(x) && x is not '-' and not '_'))
            throw new ArgumentException("BackupId 包含非法字符", nameof(backupId));
        return EnsureInsideDirectory(Path.Combine(_directory, $"{backupId}.manifest.json"));
    }

    private string ResolvePayloadPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentException("备份文件名非法", nameof(fileName));
        return EnsureInsideDirectory(Path.Combine(_directory, fileName));
    }

    private string EnsureInsideDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = _directory.EndsWith(Path.DirectorySeparatorChar)
            ? _directory
            : _directory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份路径越界");
        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // 下一次保留策略清理会重试，不影响本轮备份。
        }
    }
}
