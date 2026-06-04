namespace Wcs.Core.PlcSubsystem.Examples;

[PlcBlock("PLC3", 1)]
public struct PLC3_DB1_RobotStatus
{
    public bool ROBOT01_Busy; public bool ROBOT01_Gripped; public bool ROBOT01_Fault; public bool ROBOT01_PalletPresent;
    public short ROBOT01_AxisPos;
    public bool ROBOT02_Busy; public bool ROBOT02_Gripped; public bool ROBOT02_Fault; public bool ROBOT02_PalletPresent;
    public short ROBOT02_AxisPos;
    public bool ROBOT03_Busy; public bool ROBOT03_Gripped; public bool ROBOT03_Fault; public bool ROBOT03_PalletPresent;
    public short ROBOT03_AxisPos;
    public bool ROBOT04_Busy; public bool ROBOT04_Gripped; public bool ROBOT04_Fault; public bool ROBOT04_PalletPresent;
    public short ROBOT04_AxisPos;
}

[PlcBlock("PLC3", 2)]
public struct PLC3_DB2_RobotRequest
{
    public bool ROBOT01_GripReq; public bool ROBOT01_ReleaseReq; public bool ROBOT01_MoveReq; public short ROBOT01_TargetPos;
    public bool ROBOT02_GripReq; public bool ROBOT02_ReleaseReq; public bool ROBOT02_MoveReq; public short ROBOT02_TargetPos;
    public bool ROBOT03_GripReq; public bool ROBOT03_ReleaseReq; public bool ROBOT03_MoveReq; public short ROBOT03_TargetPos;
    public bool ROBOT04_GripReq; public bool ROBOT04_ReleaseReq; public bool ROBOT04_MoveReq; public short ROBOT04_TargetPos;
}

[PlcBlock("PLC3", 3)]
public struct PLC3_DB3_RobotAlarm
{
    public bool ROBOT01_Alarm; public byte ROBOT01_AlarmCode;
    public bool ROBOT02_Alarm; public byte ROBOT02_AlarmCode;
    public bool ROBOT03_Alarm; public byte ROBOT03_AlarmCode;
    public bool ROBOT04_Alarm; public byte ROBOT04_AlarmCode;
}

[PlcBlock("PLC3", 101)]
public struct RobotControlCommand
{
    [PlcOffset(0, 0)] public bool GripCmd1; [PlcOffset(0, 1)] public bool ReleaseCmd1;
    [PlcOffset(0, 2)] public bool MoveCmd1; [PlcOffset(2)] public short TargetPos1;
    [PlcOffset(4, 0)] public bool GripCmd2; [PlcOffset(4, 1)] public bool ReleaseCmd2;
    [PlcOffset(4, 2)] public bool MoveCmd2; [PlcOffset(6)] public short TargetPos2;
    [PlcOffset(8, 0)] public bool GripCmd3; [PlcOffset(8, 1)] public bool ReleaseCmd3;
    [PlcOffset(8, 2)] public bool MoveCmd3; [PlcOffset(10)] public short TargetPos3;
    [PlcOffset(12, 0)] public bool GripCmd4; [PlcOffset(12, 1)] public bool ReleaseCmd4;
    [PlcOffset(12, 2)] public bool MoveCmd4; [PlcOffset(14)] public short TargetPos4;
}
