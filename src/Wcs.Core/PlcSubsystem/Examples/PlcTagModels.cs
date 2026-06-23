// ====================================================================
// S7CommPlus 标签模型 — 使用 [PlcStruct] + [PlcTag] 特性
//
// 读取链路：TagPollingService → PlcTagSerializer → S7CommPlusPlcClient
//   → S7CommPlusConnection.getPlcTagBySymbol() → ReadTags()
//
// Json 配置：
//   "PlcTagPolls": [
//     { "StructType": "Wcs.Core.PlcSubsystem.Examples.TagConveyorStatus, Wcs.Core" }
//   ]
// ====================================================================

namespace Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// 西门子 S7-1500 标签式输送线状态
/// 标签名对应 PLC 中的符号名，通过 S7CommPlus 协议读取
/// </summary>
[PlcStruct("DB1", RefreshRateMs = 500)]
public class TagConveyorStatus
{
    [PlcTag("DB1.CV01_DriveReady")]     public bool DriveReady { get; set; }
    [PlcTag("DB1.CV01_PalletArrived")]  public bool PalletArrived { get; set; }
    [PlcTag("DB1.CV01_Fault")]          public bool Fault { get; set; }
    [PlcTag("DB1.CV01_Busy")]           public bool Busy { get; set; }
    [PlcTag("DB1.CV01_Speed")]          public short Speed { get; set; }
    [PlcTag("DB1.CV02_DriveReady")]     public bool CV02_DriveReady { get; set; }
    [PlcTag("DB1.CV02_PalletArrived")]  public bool CV02_PalletArrived { get; set; }
    [PlcTag("DB1.CV02_Fault")]          public bool CV02_Fault { get; set; }
    [PlcTag("DB1.CV02_Busy")]           public bool CV02_Busy { get; set; }
    [PlcTag("DB1.CV02_Speed")]          public short CV02_Speed { get; set; }
}

/// <summary>
/// 标签式控制命令
/// 写入时通过 PlcTagSerializer.WriteAsync() → S7CommPlusPlcClient
/// </summary>
[PlcStruct("DB101")]
public class TagControlCommand
{
    [PlcTag("DB101.StartStation1")]    public bool StartStation1 { get; set; }
    [PlcTag("DB101.StopStation1")]     public bool StopStation1 { get; set; }
    [PlcTag("DB101.ResetStation1")]    public bool ResetStation1 { get; set; }
    [PlcTag("DB101.SpeedSetpoint1")]   public short SpeedSetpoint1 { get; set; }
    [PlcTag("DB101.StartStation2")]    public bool StartStation2 { get; set; }
    [PlcTag("DB101.StopStation2")]     public bool StopStation2 { get; set; }
    [PlcTag("DB101.SpeedSetpoint2")]   public short SpeedSetpoint2 { get; set; }
}
