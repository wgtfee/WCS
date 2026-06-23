namespace Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 标签轮询配置（对应 appsettings.json → PlcTagPolls）
///
/// 用法：
/// ```json
/// {
///   "PlcTagPolls": [
///     {
///       "StructType": "Wcs.Core.PlcSubsystem.Examples.ConveyorStatus, Wcs.Core",
///       "PollIntervalMs": 500
///     }
///   ]
/// }
/// ```
/// </summary>
public class TagPollConfig
{
    /// <summary>
    /// C# class 类型全名（含命名空间和程序集）
    /// 该类必须标记 [PlcStruct] 和 [PlcTag] 特性
    /// </summary>
    public string StructType { get; set; } = string.Empty;

    /// <summary>
    /// 轮询间隔（毫秒），默认从 [PlcStruct(RefreshRateMs)] 读取
    /// 设置此值将覆盖特性中的定义
    /// </summary>
    public int PollIntervalMs { get; set; }
}
