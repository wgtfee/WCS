using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Core.Persistence;

/// <summary>
/// 任务数据库查询接口 — 从数据库读取任务运行时/历史数据
/// </summary>
public interface ITaskQueryService
{
    /// <summary>从 Wcs_TaskRun 读取当前持久化的任务运行记录</summary>
    Task<List<TaskRunEntity>> GetTaskRunsAsync(CancellationToken ct = default);

    /// <summary>从 Wcs_TaskHistory 分页查询历史任务</summary>
    Task<(List<TaskHistoryEntity> Items, int Total)> GetTaskHistoryAsync(
        DateTime? from, DateTime? to, string? status,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default);
}
