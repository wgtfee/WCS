using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Core.Persistence;

/// <summary>
/// 报警数据库查询接口 — 从数据库读取报警运行时/历史数据
/// </summary>
public interface IAlarmQueryService
{
    /// <summary>从 Wcs_AlarmRuntime 读取当前持久化的报警状态（系统重启后恢复用）</summary>
    Task<List<AlarmRuntimeEntity>> GetRuntimeAlarmsAsync(CancellationToken ct = default);

    /// <summary>从 Wcs_AlarmHistory 分页查询历史报警记录</summary>
    Task<(List<AlarmHistoryEntity> Items, int Total)> GetAlarmHistoryAsync(
        DateTime? from, DateTime? to, string? level,
        int page = 1, int pageSize = 50,
        CancellationToken ct = default);
}
