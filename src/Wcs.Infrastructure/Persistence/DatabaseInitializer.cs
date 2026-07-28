namespace Wcs.Infrastructure.Persistence;

using SqlSugar;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.Telemetry;
using Wcs.Infrastructure.AnomalyDetection.HealthScoring;

public interface IDatabaseInitializer
{
    Task<bool> EnsureDatabaseAsync(CancellationToken cancellationToken = default);
}

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
            cancellationToken.ThrowIfCancellationRequested();
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true
            });

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
                typeof(TaskEventEntity),
                typeof(PlcTelemetryEntity),
                typeof(PlcAnomalyEntity),
                typeof(AssetHealthScoreEntity),
                typeof(TransportConfigurationEntity),
                typeof(TransportJournalEntity),
                typeof(TransportGovernedOperationEntity),
                typeof(TransportAuditEntity),
                typeof(TransportPlcSignalMapEntity),
                typeof(TransportRuntimeStateEntity),
                typeof(TransportCommissioningEntity)
            );

            db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcTelemetry_Time' AND object_id = OBJECT_ID('Wcs_PlcTelemetry'))
    CREATE INDEX IX_Wcs_PlcTelemetry_Time ON Wcs_PlcTelemetry(TimestampUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcTelemetry_Signal' AND object_id = OBJECT_ID('Wcs_PlcTelemetry'))
    CREATE INDEX IX_Wcs_PlcTelemetry_Signal ON Wcs_PlcTelemetry(PlcName, DeviceId, SignalName, TimestampUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcAnomaly_Time' AND object_id = OBJECT_ID('Wcs_PlcAnomaly'))
    CREATE INDEX IX_Wcs_PlcAnomaly_Time ON Wcs_PlcAnomaly(StartTimeUtc, Status);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcAnomaly_Device' AND object_id = OBJECT_ID('Wcs_PlcAnomaly'))
    CREATE INDEX IX_Wcs_PlcAnomaly_Device ON Wcs_PlcAnomaly(DeviceId, SignalName, StartTimeUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcAnomaly_Key' AND object_id = OBJECT_ID('Wcs_PlcAnomaly'))
    CREATE INDEX IX_Wcs_PlcAnomaly_Key ON Wcs_PlcAnomaly(AnomalyKey, Status);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AssetHealthScore_PointId' AND object_id = OBJECT_ID('Wcs_AssetHealthScore'))
    CREATE UNIQUE INDEX UX_Wcs_AssetHealthScore_PointId ON Wcs_AssetHealthScore(PointId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AssetHealthScore_AssetTime' AND object_id = OBJECT_ID('Wcs_AssetHealthScore'))
    CREATE INDEX IX_Wcs_AssetHealthScore_AssetTime ON Wcs_AssetHealthScore(AssetId, RecordedAtUtc DESC, Sequence DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AssetHealthScore_Time' AND object_id = OBJECT_ID('Wcs_AssetHealthScore'))
    CREATE INDEX IX_Wcs_AssetHealthScore_Time ON Wcs_AssetHealthScore(RecordedAtUtc);");

            _logger.LogInformation("数据库和所有表已就绪 (21 张)");
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "数据库初始化失败");
            throw;
        }
    }
}