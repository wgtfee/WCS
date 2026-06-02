namespace Wcs.Core.Common.Enums;

/// <summary>
/// 系统状态枚举
/// </summary>
public enum SystemStatus
{
    /// <summary>
    /// 离线
    /// </summary>
    Offline = 0,

    /// <summary>
    /// 初始化中
    /// </summary>
    Initializing = 1,

    /// <summary>
    /// 运行中
    /// </summary>
    Running = 2,

    /// <summary>
    /// 暂停
    /// </summary>
    Paused = 3,

    /// <summary>
    /// 错误
    /// </summary>
    Error = 4,

    /// <summary>
    /// 停止中
    /// </summary>
    Stopping = 5
}
