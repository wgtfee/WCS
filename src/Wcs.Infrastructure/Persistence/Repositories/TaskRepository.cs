namespace Wcs.Infrastructure.Persistence.Repositories;

using Dapper;
using Microsoft.Data.SqlClient;

/// <summary>
/// 任务运行时 Dapper 仓库
/// </summary>
public class TaskRepository
{
    private readonly string _connectionString;

    public TaskRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task SaveTaskRuntimeAsync(TaskRuntimeEntity entity)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            MERGE TaskRuntimes AS target
            USING (SELECT @TaskId AS TaskId) AS source
            ON target.TaskId = source.TaskId
            WHEN MATCHED THEN
                UPDATE SET Status = @Status, Priority = @Priority, RouteId = @RouteId,
                    StartTime = @StartTime, EndTime = @EndTime, Parameters = @Parameters
            WHEN NOT MATCHED THEN
                INSERT (TaskId, Status, Priority, RouteId, StartTime, EndTime, Parameters)
                VALUES (@TaskId, @Status, @Priority, @RouteId, @StartTime, @EndTime, @Parameters);",
            entity);
    }

    public async Task ArchiveTaskAsync(TaskHistoryEntity history)
    {
        using var conn = new SqlConnection(_connectionString);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(@"
            INSERT INTO TaskHistories (TaskId, RouteId, Priority, StartTime, EndTime, Success, ErrorMessage)
            VALUES (@TaskId, @RouteId, @Priority, @StartTime, @EndTime, @Success, @ErrorMessage)", history, tx);
        await conn.ExecuteAsync("DELETE FROM TaskRuntimes WHERE TaskId = @TaskId", new { history.TaskId }, tx);
        tx.Commit();
    }
}

/// <summary>
/// 报警 Dapper 仓库
/// </summary>
    /// <summary>
    /// 保存设备运行时状态 (upsert)
    /// </summary>
    public async Task SaveDeviceRuntimeAsync(DeviceRuntimeEntity entity)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            MERGE DeviceRuntimes AS target
            USING (SELECT @DeviceId AS DeviceId) AS source
            ON target.DeviceId = source.DeviceId
            WHEN MATCHED THEN
                UPDATE SET Status = @Status, LastUpdateTime = @LastUpdateTime, Properties = @Properties
            WHEN NOT MATCHED THEN
                INSERT (DeviceId, Status, LastUpdateTime, Properties)
                VALUES (@DeviceId, @Status, @LastUpdateTime, @Properties);",
            entity);
    }


public class AlarmRepository
{
    private readonly string _connectionString;

    public AlarmRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task SaveAlarmRuntimeAsync(AlarmRuntimeEntity entity)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            MERGE AlarmRuntimes AS target
            USING (SELECT @AlarmId AS AlarmId) AS source
            ON target.AlarmId = source.AlarmId
            WHEN MATCHED THEN
                UPDATE SET Status = @Status, Level = @Level, Message = @Message, RecoverTime = @RecoverTime
            WHEN NOT MATCHED THEN
                INSERT (AlarmId, AlarmCode, Status, Level, Message, OccurTime, RecoverTime)
                VALUES (@AlarmId, @AlarmCode, @Status, @Level, @Message, @OccurTime, @RecoverTime);",
            entity);
    }
}

/// <summary>
/// 任务事件 Dapper 仓库 - 只追加
/// </summary>
public class TaskEventRepository
{
    private readonly string _connectionString;

    public TaskEventRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task AppendAsync(TaskEventEntity entity)
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO TaskEvents (TaskId, EventType, Payload, CreateTime)
            VALUES (@TaskId, @EventType, @Payload, @CreateTime)", entity);
    }
}
