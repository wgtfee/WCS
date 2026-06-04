namespace Wcs.Core.PlcSubsystem.Examples;

// ====================================================================
// PLC1 — 输送线控制系统 | 192.168.0.1 | Rack:0 Slot:1
//
// 读：
//   DB1(40B) 输送线状态    → PLC1_DB1_ConveyorStatus
//   DB2(20B) 输送线任务请求 → PLC1_DB2_ConveyorRequest
//   DB3(20B) 输送线报警     → PLC1_DB3_ConveyorAlarm
// 写：
//   DB101    输送线控制     → ConveyorControlCommand
// ====================================================================

[PlcBlock("PLC1", 1)]
public struct PLC1_DB1_ConveyorStatus
{
    public bool CV01_DriveReady; 
    public bool CV01_PalletArrived; 
    public bool CV01_Fault; 
    public bool CV01_Busy;
    public short CV01_Speed;
    public bool CV02_DriveReady; 
    public bool CV02_PalletArrived; 
    public bool CV02_Fault; 
    public bool CV02_Busy;
    public short CV02_Speed;
    public bool CV03_DriveReady; 
    public bool CV03_PalletArrived; 
    public bool CV03_Fault; 
    public bool CV03_Busy;
    public short CV03_Speed;
    public bool CV04_DriveReady; 
    public bool CV04_PalletArrived; 
    public bool CV04_Fault; 
    public bool CV04_Busy;
    public short CV04_Speed;
    public bool CV05_DriveReady; 
    public bool CV05_PalletArrived; 
    public bool CV05_Fault; 
    public bool CV05_Busy;
    public short CV05_Speed;
    public bool CV06_DriveReady; 
    public bool CV06_PalletArrived; 
    public bool CV06_Fault; 
    public bool CV06_Busy;
    public short CV06_Speed;
    public bool CV07_DriveReady; 
    public bool CV07_PalletArrived; 
    public bool CV07_Fault; 
    public bool CV07_Busy;
    public short CV07_Speed;
    public bool CV08_DriveReady; 
    public bool CV08_PalletArrived; 
    public bool CV08_Fault; 
    public bool CV08_Busy;
    public short CV08_Speed;
    public bool CV09_DriveReady; 
    public bool CV09_PalletArrived; 
    public bool CV09_Fault; 
    public bool CV09_Busy;
    public short CV09_Speed;
    public bool CV10_DriveReady; 
    public bool CV10_PalletArrived; 
    public bool CV10_Fault; 
    public bool CV10_Busy;
    public short CV10_Speed;
}

[PlcBlock("PLC1", 2)]
public struct PLC1_DB2_ConveyorRequest
{
    public bool CV01_RequestOut; public bool CV01_RequestIn; public byte CV01_TargetStation;
    public bool CV02_RequestOut; public bool CV02_RequestIn; public byte CV02_TargetStation;
    public bool CV03_RequestOut; public bool CV03_RequestIn; public byte CV03_TargetStation;
    public bool CV04_RequestOut; public bool CV04_RequestIn; public byte CV04_TargetStation;
    public bool CV05_RequestOut; public bool CV05_RequestIn; public byte CV05_TargetStation;
    public bool CV06_RequestOut; public bool CV06_RequestIn; public byte CV06_TargetStation;
    public bool CV07_RequestOut; public bool CV07_RequestIn; public byte CV07_TargetStation;
    public bool CV08_RequestOut; public bool CV08_RequestIn; public byte CV08_TargetStation;
    public bool CV09_RequestOut; public bool CV09_RequestIn; public byte CV09_TargetStation;
    public bool CV10_RequestOut; public bool CV10_RequestIn; public byte CV10_TargetStation;
}

[PlcBlock("PLC1", 3)]
public struct PLC1_DB3_ConveyorAlarm
{
    public bool CV01_Alarm; public byte CV01_AlarmCode;
    public bool CV02_Alarm; public byte CV02_AlarmCode;
    public bool CV03_Alarm; public byte CV03_AlarmCode;
    public bool CV04_Alarm; public byte CV04_AlarmCode;
    public bool CV05_Alarm; public byte CV05_AlarmCode;
    public bool CV06_Alarm; public byte CV06_AlarmCode;
    public bool CV07_Alarm; public byte CV07_AlarmCode;
    public bool CV08_Alarm; public byte CV08_AlarmCode;
    public bool CV09_Alarm; public byte CV09_AlarmCode;
    public bool CV10_Alarm; public byte CV10_AlarmCode;
}

[PlcBlock("PLC1", 101)]
public struct ConveyorControlCommand
{
    [PlcOffset(0, 0)] public bool StartStation1; [PlcOffset(0, 1)] public bool StopStation1;
    [PlcOffset(0, 2)] public bool ResetStation1; [PlcOffset(2)] public short SpeedSetpoint1;
    [PlcOffset(4, 0)] public bool StartStation2; [PlcOffset(4, 1)] public bool StopStation2;
    [PlcOffset(4, 2)] public bool ResetStation2; [PlcOffset(6)] public short SpeedSetpoint2;
    [PlcOffset(8, 0)] public bool StartStation3; [PlcOffset(8, 1)] public bool StopStation3;
    [PlcOffset(8, 2)] public bool ResetStation3; [PlcOffset(10)] public short SpeedSetpoint3;
}
