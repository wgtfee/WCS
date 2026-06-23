namespace Wcs.Core.PlcSubsystem.Label;

/// <summary>
/// 标签轮询配置（对应 appsettings.json → PlcTagPolls）
///
/// 用法：只需指定 StructType，轮询间隔从 [PlcStruct(RefreshRateMs)] 特性读取
/// ```json
/// {
///   "PlcTagPolls": [
///     { "StructType": "Wcs.Core.PlcSubsystem.Examples.ConveyorStatus, Wcs.Core" }
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
}
