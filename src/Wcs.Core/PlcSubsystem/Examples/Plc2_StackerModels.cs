namespace Wcs.Core.PlcSubsystem.Examples;

[PlcBlock("PLC2", 1)]
public struct PLC2_DB1_StackerStatus
{
    public bool ASRS01_Busy; public bool ASRS01_Fault; public bool ASRS01_AutoMode; public bool ASRS01_PositionArrived;
    public short ASRS01_CurColumn; public short ASRS01_CurRow;
    public bool ASRS02_Busy; public bool ASRS02_Fault; public bool ASRS02_AutoMode; public bool ASRS02_PositionArrived;
    public short ASRS02_CurColumn; public short ASRS02_CurRow;
    public bool ASRS03_Busy; public bool ASRS03_Fault; public bool ASRS03_AutoMode; public bool ASRS03_PositionArrived;
    public short ASRS03_CurColumn; public short ASRS03_CurRow;
    public bool ASRS04_Busy; public bool ASRS04_Fault; public bool ASRS04_AutoMode; public bool ASRS04_PositionArrived;
    public short ASRS04_CurColumn; public short ASRS04_CurRow;
}

[PlcBlock("PLC2", 2)]
public struct PLC2_DB2_StackerRequest
{
    public bool ASRS01_StoreReq; public bool ASRS01_RetrieveReq; public short ASRS01_TargetColumn; public short ASRS01_TargetRow;
    public bool ASRS02_StoreReq; public bool ASRS02_RetrieveReq; public short ASRS02_TargetColumn; public short ASRS02_TargetRow;
    public bool ASRS03_StoreReq; public bool ASRS03_RetrieveReq; public short ASRS03_TargetColumn; public short ASRS03_TargetRow;
    public bool ASRS04_StoreReq; public bool ASRS04_RetrieveReq; public short ASRS04_TargetColumn; public short ASRS04_TargetRow;
}

[PlcBlock("PLC2", 3)]
public struct PLC2_DB3_StackerAlarm
{
    public bool ASRS01_Alarm; public byte ASRS01_AlarmCode; public short ASRS01_FaultDetail;
    public bool ASRS02_Alarm; public byte ASRS02_AlarmCode; public short ASRS02_FaultDetail;
    public bool ASRS03_Alarm; public byte ASRS03_AlarmCode; public short ASRS03_FaultDetail;
    public bool ASRS04_Alarm; public byte ASRS04_AlarmCode; public short ASRS04_FaultDetail;
}

[PlcBlock("PLC2", 201)]
public struct StackerControlCommand
{
    [PlcOffset(0, 0)] public bool StoreCmd1; [PlcOffset(0, 1)] public bool RetrieveCmd1;
    [PlcOffset(0, 2)] public bool ResetCmd1; [PlcOffset(2)] public short TargetCol1; [PlcOffset(4)] public short TargetRow1;
    [PlcOffset(6, 0)] public bool StoreCmd2; [PlcOffset(6, 1)] public bool RetrieveCmd2;
    [PlcOffset(6, 2)] public bool ResetCmd2; [PlcOffset(8)] public short TargetCol2; [PlcOffset(10)] public short TargetRow2;
}
