using SqlSugar;
using Wcs.Core.Persistence;
using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Infrastructure.Persistence.Services;

/// <summary>
/// 设备数据库查询服务实现 — 基于 SqlSugar
/// </summary>
public class DeviceQueryService : IDeviceQueryService
{
    private readonly ISqlSugarClient _db;

    public DeviceQueryService(ISqlSugarClient db)
    {
        _db = db;
    }

    public async Task<List<DeviceRuntimeEntity>> GetDeviceRuntimesAsync(CancellationToken ct = default)
    {
        return await _db.Queryable<DeviceRuntimeEntity>()
            .ToListAsync(ct);
    }
}
