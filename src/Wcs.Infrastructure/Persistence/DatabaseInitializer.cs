namespace Wcs.Infrastructure.Persistence;

using SqlSugar;
using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem.Examples;

public interface IDatabaseInitializer
{
    Task<bool> EnsureDatabaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 数据库初始化器 — 使用 SqlSugar CodeFirst 自动建库建表
/// 替代 EF Core 实现
/// </summary>
public class DatabaseInitializer : IDatabaseInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(string connectionString, ILogger<DatabaseInitializer> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<bool> EnsureDatabaseAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("检查数据库状态 (SqlSugar)...");

        try
        {
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true
            });

            // CodeFirst: 不存在则创建所有表
            db.CodeFirst.InitTables(
                typeof(TaskRunEntity),
                typeof(TransportHistoryEntity),
                typeof(CommandLogEntity),
                typeof(DeviceStateLogEntity),
                typeof(PlcWriteLogEntity),
                typeof(DeviceRuntimeEntity),
                typeof(TaskRuntimeEntity),
                typeof(AlarmRuntimeEntity),
                typeof(TaskHistoryEntity),
                typeof(AlarmHistoryEntity),
                typeof(TaskEventEntity)
            );

            _logger.LogInformation("数据库和所有表已就绪 (11 张)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "数据库初始化失败");
            throw;
        }
    }
}
