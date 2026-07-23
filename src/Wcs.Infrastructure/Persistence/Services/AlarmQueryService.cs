using SqlSugar;
using Wcs.Core.Persistence;
using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Infrastructure.Persistence.Services;

/// <summary>
/// 报警数据库查询服务实现 — 基于 SqlSugar
/// </summary>
public class AlarmQueryService : IAlarmQueryService
{
    private readonly string _connectionString;

    public AlarmQueryService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<AlarmRuntimeEntity>> GetRuntimeAlarmsAsync(CancellationToken ct = default)
    {
        using var db = CreateDb();
        return await db.Queryable<AlarmRuntimeEntity>()
            .ToListAsync(ct);
    }

    public async Task<(List<AlarmHistoryEntity> Items, int Total)> GetAlarmHistoryAsync(
        DateTime? from, DateTime? to, string? level,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        using var db = CreateDb();
        var query = db.Queryable<AlarmHistoryEntity>();

        if (from.HasValue)
            query = query.Where(e => e.StartTime >= from.Value);
        if (to.HasValue)
            query = query.Where(e => e.StartTime <= to.Value);
        if (!string.IsNullOrWhiteSpace(level))
            query = query.Where(e => e.Level == level);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.StartTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    private SqlSugarClient CreateDb() =>
        new(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });
}
