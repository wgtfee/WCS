namespace Wcs.Core.TransportScheduling;

using System.Collections.Concurrent;

public interface ITransportLogicalBackupStorage
{
    Task SaveAsync(
        TransportLogicalBackupManifest manifest,
        byte[] payload,
        CancellationToken cancellationToken = default);

    Task<TransportLogicalBackupContent?> LoadAsync(
        string backupId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransportLogicalBackupManifest>> GetManifestsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default);

    Task<int> TrimAsync(
        int retentionCount,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryTransportLogicalBackupStorage : ITransportLogicalBackupStorage
{
    private readonly ConcurrentDictionary<string, TransportLogicalBackupContent> _items =
        new(StringComparer.Ordinal);

    public Task SaveAsync(
        TransportLogicalBackupManifest manifest,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        _items[manifest.BackupId] = new TransportLogicalBackupContent
        {
            Manifest = manifest,
            Payload = payload.ToArray()
        };
        return Task.CompletedTask;
    }

    public Task<TransportLogicalBackupContent?> LoadAsync(
        string backupId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_items.TryGetValue(backupId, out var content))
            return Task.FromResult<TransportLogicalBackupContent?>(null);
        return Task.FromResult<TransportLogicalBackupContent?>(new TransportLogicalBackupContent
        {
            Manifest = content.Manifest,
            Payload = content.Payload.ToArray()
        });
    }

    public Task<IReadOnlyList<TransportLogicalBackupManifest>> GetManifestsAsync(
        int maxCount = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TransportLogicalBackupManifest> result = _items.Values
            .Select(x => x.Manifest)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(maxCount, 1, 1000))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<int> TrimAsync(
        int retentionCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        retentionCount = Math.Max(1, retentionCount);
        var remove = _items.Values
            .OrderByDescending(x => x.Manifest.CreatedAtUtc)
            .Skip(retentionCount)
            .Select(x => x.Manifest.BackupId)
            .ToArray();
        var removed = 0;
        foreach (var backupId in remove)
        {
            if (_items.TryRemove(backupId, out _))
                removed++;
        }
        return Task.FromResult(removed);
    }
}
