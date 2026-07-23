using SqlSugar;
using Wcs.Core.Persistence;
using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Infrastructure.Persistence.Services;

/// <summary>
/// 设备数据库查询服务实现 — 基于 SqlSugar
/// </summary>
public class DeviceQueryService : IDeviceQueryService
{
    private readonly string _connectionString;

    public DeviceQueryService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<List<DeviceRuntimeEntity>> GetDeviceRuntimesAsync(CancellationToken ct = default)
    {
        using var db = CreateDb();
        return await db.Queryable<DeviceRuntimeEntity>()
            .ToListAsync(ct);
    }

    private SqlSugarClient CreateDb() =>
        new(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });
}
