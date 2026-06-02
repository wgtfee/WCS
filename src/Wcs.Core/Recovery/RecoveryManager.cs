namespace Wcs.Core.Recovery;

using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 快照仓库接口
/// </summary>
public interface ISnapshotRepository
{
    /// <summary>
    /// 保存快照
    /// </summary>
    Task SaveSnapshotAsync(StateSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// 加载最新快照
    /// </summary>
    Task<StateSnapshot?> LoadLatestSnapshotAsync(CancellationToken ct = default);

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
/// 生产环境应替换为数据库持久化
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

    public async Task SaveSnapshotAsync(StateSnapshot snapshot, CancellationToken ct = default)
    {
        var fileName = $"{FilePrefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}{FileExtension}";
        var filePath = Path.Combine(_storagePath, fileName);

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false
        });

        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public async Task<StateSnapshot?> LoadLatestSnapshotAsync(CancellationToken ct = default)
    {
        var files = Directory.GetFiles(_storagePath, $"{FilePrefix}*{FileExtension}")
            .OrderByDescending(f => f)
            .ToList();

        if (files.Count == 0) return null;

        var json = await File.ReadAllTextAsync(files[0], ct);
        return System.Text.Json.JsonSerializer.Deserialize<StateSnapshot>(json);
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
            try
            {
                File.Delete(file);
                removed++;
            }
            catch { }
        }

        await Task.CompletedTask;
        return removed;
    }
}

/// <summary>
/// 恢复管理器
/// </summary>
public interface IRecoveryManager
{
    /// <summary>
    /// 执行系统恢复
    /// </summary>
    Task<RecoveryResult> RecoverAsync(CancellationToken ct = default);

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
    public int RestoredDevices { get; set; }
    public int RestoredTasks { get; set; }
    public int RestoredAlarms { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// 恢复管理器实现
/// </summary>
public class RecoveryManager : IRecoveryManager
{
    private readonly IStateCenter _stateCenter;
    private readonly ISnapshotRepository _snapshotRepo;

    public RecoveryManager(IStateCenter stateCenter, ISnapshotRepository snapshotRepo)
    {
        _stateCenter = stateCenter ?? throw new ArgumentNullException(nameof(stateCenter));
        _snapshotRepo = snapshotRepo ?? throw new ArgumentNullException(nameof(snapshotRepo));
    }

    public async Task<bool> NeedsRecoveryAsync(CancellationToken ct = default)
    {
        var snapshot = await _snapshotRepo.LoadLatestSnapshotAsync(ct);
        return snapshot != null;
    }

    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct = default)
    {
        var result = new RecoveryResult();

        var snapshot = await _snapshotRepo.LoadLatestSnapshotAsync(ct);
        if (snapshot == null)
        {
            result.Message = "没有找到可恢复的快照";
            return result;
        }

        _stateCenter.RestoreFromSnapshot(snapshot);

        result.Success = true;
        result.RestoredDevices = snapshot.DeviceStates.Count;
        result.RestoredTasks = snapshot.TaskRuntimes.Count;
        result.RestoredAlarms = snapshot.AlarmStates.Count;
        result.Message = $"从快照 {snapshot.SnapshotTime:O} 恢复成功";

        return result;
    }
}
