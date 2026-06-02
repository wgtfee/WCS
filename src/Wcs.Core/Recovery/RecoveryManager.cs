namespace Wcs.Core.Recovery;

using System.Collections.Concurrent;
using System.Text.Json;
using Wcs.Core.Common.Interfaces;
using Wcs.Core.EventBus.Persistence;
using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 快照仓库接口
/// </summary>
public interface ISnapshotRepository
{
    /// <summary>
    /// 保存快照
    /// </summary>
    Task SaveSnapshotAsync(SystemSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// 加载最新快照
    /// </summary>
    Task<SystemSnapshot?> LoadLatestSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取快照列表
    /// </summary>
    Task<IEnumerable<SnapshotMetadata>> GetSnapshotListAsync(CancellationToken ct = default);

    /// <summary>
    /// 清理旧快照
    /// </summary>
    Task<int> CleanupOldSnapshotsAsync(int keepCount, CancellationToken ct = default);
}

/// <summary>
/// 快照元数据
/// </summary>
public class SnapshotMetadata
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreateTime { get; set; }
    public int DeviceCount { get; set; }
    public int TaskCount { get; set; }
    public int AlarmCount { get; set; }
}

/// <summary>
/// 基于内存文件系统的快照仓库
/// </summary>
public class SnapshotRepository : ISnapshotRepository
{
    private readonly string _storagePath;
    private const string FilePrefix = "wcs_snapshot_";
    private const string FileExtension = ".json";

    public SnapshotRepository(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots");
        Directory.CreateDirectory(_storagePath);
    }

    public async Task SaveSnapshotAsync(SystemSnapshot snapshot, CancellationToken ct = default)
    {
        var fileName = $"{FilePrefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}{FileExtension}";
        var filePath = Path.Combine(_storagePath, fileName);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public async Task<SystemSnapshot?> LoadLatestSnapshotAsync(CancellationToken ct = default)
    {
        var files = Directory.GetFiles(_storagePath, $"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f)
            .ToList();

        if (files.Count == 0) return null;

        var json = await File.ReadAllTextAsync(files[0], ct);

        // 尝试新格式 (SystemSnapshot)
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("ModuleSnapshots", out _))
        {
            return JsonSerializer.Deserialize<SystemSnapshot>(json);
        }

        // 旧格式 (StateSnapshot) — 转换为 SystemSnapshot，仅包含 StateCenter
        var stateSnapshot = JsonSerializer.Deserialize<StateSnapshot>(json);
        if (stateSnapshot != null)
        {
            return new SystemSnapshot
            {
                Timestamp = stateSnapshot.SnapshotTime,
                ModuleSnapshots = new()
                {
                    ["StateCenter"] = JsonSerializer.SerializeToElement(stateSnapshot)
                }
            };
        }

        return null;
    }

    public Task<IEnumerable<SnapshotMetadata>> GetSnapshotListAsync(CancellationToken ct = default)
    {
        var files = Directory.GetFiles(_storagePath, $"{FilePrefix}*{FileExtension}")
            .Select(f =>
            {
                var fi = new FileInfo(f);
                return new SnapshotMetadata
                {
                    Id = fi.Name,
                    CreateTime = fi.CreationTimeUtc
                };
            })
            .OrderByDescending(m => m.CreateTime)
            .ToList();

        return Task.FromResult<IEnumerable<SnapshotMetadata>>(files);
    }

    public async Task<int> CleanupOldSnapshotsAsync(int keepCount, CancellationToken ct = default)
    {
        var files = Directory.GetFiles(_storagePath, $"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f)
            .ToList();

        if (files.Count <= keepCount) return 0;

        var removed = 0;
        foreach (var file in files.Skip(keepCount))
        {
            try { File.Delete(file); removed++; }
            catch { }
        }

        await Task.CompletedTask;
        return removed;
    }
}

/// <summary>
/// 复合系统快照 — 包含多个模块的独立快照数据
/// </summary>
public class SystemSnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, JsonElement> ModuleSnapshots { get; set; } = new();
}

/// <summary>
/// 恢复管理器接口
/// </summary>
public interface IRecoveryManager
{
    /// <summary>
    /// 执行系统恢复
    /// </summary>
    Task<RecoveryResult> RecoverAsync(CancellationToken ct = default);

    /// <summary>
    /// 保存当前系统快照
    /// </summary>
    Task SaveSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// 检查是否需要恢复
    /// </summary>
    Task<bool> NeedsRecoveryAsync(CancellationToken ct = default);
}

/// <summary>
/// 恢复结果
/// </summary>
public class RecoveryResult
{
    public bool Success { get; set; }
    public DateTime RecoveryTime { get; set; } = DateTime.UtcNow;
    public List<string> RestoredModules { get; set; } = new();
    public string? Message { get; set; }
}

/// <summary>
/// 多模块恢复管理器 — 协调所有 ISnapshotProvider 的快照与恢复
/// 恢复顺序：StateCenter → ObjectTracking → AlarmCenter → TaskChain（依赖反序）
/// </summary>
public class RecoveryManager : IRecoveryManager
{
    private readonly IEnumerable<ISnapshotProvider> _providers;
    private readonly ISnapshotRepository _snapshotRepo;
    private readonly EventReplayService? _eventReplay;

    public RecoveryManager(
        IEnumerable<ISnapshotProvider> providers,
        ISnapshotRepository snapshotRepo,
        EventReplayService? eventReplay = null)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _snapshotRepo = snapshotRepo ?? throw new ArgumentNullException(nameof(snapshotRepo));
        _eventReplay = eventReplay;
    }

    public async Task<bool> NeedsRecoveryAsync(CancellationToken ct = default)
    {
        var snapshot = await _snapshotRepo.LoadLatestSnapshotAsync(ct);
        return snapshot != null;
    }

    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct = default)
    {
        var result = new RecoveryResult();

        var sysSnapshot = await _snapshotRepo.LoadLatestSnapshotAsync(ct);
        if (sysSnapshot == null)
        {
            result.Message = "No snapshot found for recovery";
            return result;
        }

        // 按 RestoreOrder 顺序恢复（依赖反序：先基础模块，后高级模块）
        var orderedProviders = _providers
            .Where(p => sysSnapshot.ModuleSnapshots.ContainsKey(p.ModuleName))
            .OrderBy(p => p.RestoreOrder)
            .ToList();

        foreach (var provider in orderedProviders)
        {
            var moduleName = provider.ModuleName;
            var element = sysSnapshot.ModuleSnapshots[moduleName];

            try
            {
                await provider.RestoreSnapshotAsync(element, ct);
                result.RestoredModules.Add(moduleName);
            }
            catch (Exception ex)
            {
                result.Message = $"Failed to restore module '{moduleName}': {ex.Message}";
                return result;
            }
        }

        // 触发事件重放（可选 — 需要 EventReplayService + IEventStore）
        if (_eventReplay != null)
        {
            try
            {
                var replayed = await _eventReplay.ReplayAsync(sysSnapshot.Timestamp, ct);
                result.Message =
                    $"System recovered: {string.Join(", ", result.RestoredModules)}, " +
                    $"events replayed: {replayed}";
            }
            catch (Exception ex)
            {
                result.Message =
                    $"System recovered: {string.Join(", ", result.RestoredModules)}, " +
                    $"but event replay failed: {ex.Message}";
            }
        }
        else
        {
            result.Message = $"System recovered: {string.Join(", ", result.RestoredModules)}";
        }

        result.Success = true;
        return result;
    }

    public async Task SaveSnapshotAsync(CancellationToken ct = default)
    {
        var sysSnapshot = new SystemSnapshot();
        foreach (var provider in _providers)
        {
            var data = await provider.CaptureSnapshotAsync(ct);
            sysSnapshot.ModuleSnapshots[provider.ModuleName] = JsonSerializer.SerializeToElement(data);
        }
        await _snapshotRepo.SaveSnapshotAsync(sysSnapshot, ct);
    }
}
