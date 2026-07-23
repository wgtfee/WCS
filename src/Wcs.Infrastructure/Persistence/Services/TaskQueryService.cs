using SqlSugar;
using Wcs.Core.Persistence;
using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Infrastructure.Persistence.Services;

/// <summary>
/// 任务数据库查询服务实现 — 基于 SqlSugar
/// </summary>
public class TaskQueryService : ITaskQueryService
{
    private readonly ISqlSugarClient _db;

    public TaskQueryService(ISqlSugarClient db)
    {
        _db = db;
    }

    public async Task<List<TaskRunEntity>> GetTaskRunsAsync(CancellationToken ct = default)
    {
        return await _db.Queryable<TaskRunEntity>()
            .OrderByDescending(e => e.CreatedTime)
            .ToListAsync(ct);
    }

    public async Task<(List<TaskHistoryEntity> Items, int Total)> GetTaskHistoryAsync(
        DateTime? from, DateTime? to, string? status,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = _db.Queryable<TaskHistoryEntity>();

        if (from.HasValue)
            query = query.Where(e => e.StartTime >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.StartTime <= to.Value);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var success = status.Equals("Success", StringComparison.OrdinalIgnoreCase)
                       || status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
            query = query.Where(e => e.Success == success);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.StartTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }
}
