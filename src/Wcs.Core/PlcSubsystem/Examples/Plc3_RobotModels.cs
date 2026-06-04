namespace Wcs.Core.PlcSubsystem.Examples;

// ====================================================================
// PLC3 — 机器人控制系统
// PLC 名称: "PLC3" | 地址: 192.168.0.3 | Rack:0 Slot:1
//
// DB1: 机器人状态 (byte[0..15], 4 台 × ~4 字节/台)
// DB2: 机器人任务请求 (byte[0..15], 4 台 × ~4 字节/台)
// DB3: 机器人报警 (byte[0..11], 4 台 × ~3 字节/台)
// DB101: 机器人控制命令 (写)
// ====================================================================

// ===================== DB1: 机器人状态 =====================

public struct PLC3_DB1_RobotStatus
{
    // === 1 号机器人 (byte 0~3) ===
    public bool ROBOT01_Busy;           // DB1.DBX0.0
    public bool ROBOT01_Gripped;        // DB1.DBX0.1
    public bool ROBOT01_Fault;          // DB1.DBX0.2
    public bool ROBOT01_PalletPresent;  // DB1.DBX0.3
    public short ROBOT01_AxisPos;       // DB1.DBW2

    // === 2 号机器人 (byte 4~7) ===
    public bool ROBOT02_Busy;           // DB1.DBX4.0
    public bool ROBOT02_Gripped;        // DB1.DBX4.1
    public bool ROBOT02_Fault;          // DB1.DBX4.2
    public bool ROBOT02_PalletPresent;  // DB1.DBX4.3
    public short ROBOT02_AxisPos;       // DB1.DBW6

    // === 3 号机器人 (byte 8~11) ===
    public bool ROBOT03_Busy;           // DB1.DBX8.0
    public bool ROBOT03_Gripped;        // DB1.DBX8.1
    public bool ROBOT03_Fault;          // DB1.DBX8.2
    public bool ROBOT03_PalletPresent;  // DB1.DBX8.3
    public short ROBOT03_AxisPos;       // DB1.DBW10

    // === 4 号机器人 (byte 12~15) ===
    public bool ROBOT04_Busy;           // DB1.DBX12.0
    public bool ROBOT04_Gripped;        // DB1.DBX12.1
    public bool ROBOT04_Fault;          // DB1.DBX12.2
    public bool ROBOT04_PalletPresent;  // DB1.DBX12.3
    public short ROBOT04_AxisPos;       // DB1.DBW14
}

// ===================== DB2: 机器人任务请求 =====================

public struct PLC3_DB2_RobotRequest
{
    // === 1 号机器人 (byte 0~3) ===
    public bool ROBOT01_GripReq;        // DB2.DBX0.0  上升沿→抓取请求
    public bool ROBOT01_ReleaseReq;     // DB2.DBX0.1  上升沿→释放请求
    public bool ROBOT01_MoveReq;        // DB2.DBX0.2  上升沿→移动请求
    public short ROBOT01_TargetPos;     // DB2.DBW2

    // === 2 号机器人 (byte 4~7) ===
    public bool ROBOT02_GripReq;
    public bool ROBOT02_ReleaseReq;
    public bool ROBOT02_MoveReq;
    public short ROBOT02_TargetPos;

    // === 3 号机器人 (byte 8~11) ===
    public bool ROBOT03_GripReq;
    public bool ROBOT03_ReleaseReq;
    public bool ROBOT03_MoveReq;
    public short ROBOT03_TargetPos;

    // === 4 号机器人 (byte 12~15) ===
    public bool ROBOT04_GripReq;
    public bool ROBOT04_ReleaseReq;
    public bool ROBOT04_MoveReq;
    public short ROBOT04_TargetPos;
}

// ===================== DB3: 机器人报警 =====================

public struct PLC3_DB3_RobotAlarm
{
    public bool ROBOT01_Alarm;          // DB3.DBX0.0
    public byte ROBOT01_AlarmCode;      // DB3.DBB1
    public bool ROBOT02_Alarm;          // DB3.DBX2.0
    public byte ROBOT02_AlarmCode;      // DB3.DBB3
    public bool ROBOT03_Alarm;          // DB3.DBX4.0
    public byte ROBOT03_AlarmCode;      // DB3.DBB5
    public bool ROBOT04_Alarm;          // DB3.DBX6.0
    public byte ROBOT04_AlarmCode;      // DB3.DBB7
}

// ===================== 写命令: 机器人控制 =====================

[PlcBlock("PLC3", 101)]
public struct RobotControlCommand
{
    [PlcOffset(0, 0)] public bool GripCmd1;        // DB101.DBX0.0
    [PlcOffset(0, 1)] public bool ReleaseCmd1;     // DB101.DBX0.1
    [PlcOffset(0, 2)] public bool MoveCmd1;        // DB101.DBX0.2
    [PlcOffset(2)]    public short TargetPos1;      // DB101.DBW2

    [PlcOffset(4, 0)] public bool GripCmd2;
    [PlcOffset(4, 1)] public bool ReleaseCmd2;
    [PlcOffset(4, 2)] public bool MoveCmd2;
    [PlcOffset(6)]    public short TargetPos2;

    [PlcOffset(8, 0)]  public bool GripCmd3;
    [PlcOffset(8, 1)]  public bool ReleaseCmd3;
    [PlcOffset(8, 2)]  public bool MoveCmd3;
    [PlcOffset(10)]   public short TargetPos3;

    [PlcOffset(12, 0)] public bool GripCmd4;
    [PlcOffset(12, 1)] public bool ReleaseCmd4;
    [PlcOffset(12, 2)] public bool MoveCmd4;
    [PlcOffset(14)]   public short TargetPos4;
}
