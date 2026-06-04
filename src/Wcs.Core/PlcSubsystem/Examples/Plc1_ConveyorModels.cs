namespace Wcs.Core.PlcSubsystem.Examples;

// ====================================================================
// PLC1 — 输送线控制系统
// PLC 名称: "PLC1" | 地址: 192.168.0.1 | Rack:0 Slot:1
//
// DB1: 输送线状态 (byte[0..39], 10 站 × ~4 字节/站)
// DB2: 输送线任务请求 (byte[0..19], 10 站 × ~2 字节/站)
// DB3: 输送线报警 (byte[0..19], 10 站 × ~2 字节/站)
// DB101: 输送线控制命令 (写)
// ====================================================================

// ===================== DB1: 输送线状态 =====================

public struct PLC1_DB1_ConveyorStatus
{
    // === 站 1 (byte 0~3) ===
    public bool CV01_DriveReady;        // DB1.DBX0.0
    public bool CV01_PalletArrived;     // DB1.DBX0.1
    public bool CV01_Fault;             // DB1.DBX0.2
    public bool CV01_Busy;              // DB1.DBX0.3
    public short CV01_Speed;            // DB1.DBW2

    // === 站 2 (byte 4~7) ===
    public bool CV02_DriveReady;        // DB1.DBX4.0
    public bool CV02_PalletArrived;     // DB1.DBX4.1
    public bool CV02_Fault;             // DB1.DBX4.2
    public bool CV02_Busy;              // DB1.DBX4.3
    public short CV02_Speed;            // DB1.DBW6

    // === 站 3 (byte 8~11) ===
    public bool CV03_DriveReady;        // DB1.DBX8.0
    public bool CV03_PalletArrived;     // DB1.DBX8.1
    public bool CV03_Fault;             // DB1.DBX8.2
    public bool CV03_Busy;              // DB1.DBX8.3
    public short CV03_Speed;            // DB1.DBW10

    // === 站 4 (byte 12~15) ===
    public bool CV04_DriveReady;        // DB1.DBX12.0
    public bool CV04_PalletArrived;     // DB1.DBX12.1
    public bool CV04_Fault;             // DB1.DBX12.2
    public bool CV04_Busy;              // DB1.DBX12.3
    public short CV04_Speed;            // DB1.DBW14

    // === 站 5 (byte 16~19) ===
    public bool CV05_DriveReady;        // DB1.DBX16.0
    public bool CV05_PalletArrived;     // DB1.DBX16.1
    public bool CV05_Fault;             // DB1.DBX16.2
    public bool CV05_Busy;              // DB1.DBX16.3
    public short CV05_Speed;            // DB1.DBW18

    // === 站 6 (byte 20~23) ===
    public bool CV06_DriveReady;        // DB1.DBX20.0
    public bool CV06_PalletArrived;     // DB1.DBX20.1
    public bool CV06_Fault;             // DB1.DBX20.2
    public bool CV06_Busy;              // DB1.DBX20.3
    public short CV06_Speed;            // DB1.DBW22

    // === 站 7 (byte 24~27) ===
    public bool CV07_DriveReady;        // DB1.DBX24.0
    public bool CV07_PalletArrived;     // DB1.DBX24.1
    public bool CV07_Fault;             // DB1.DBX24.2
    public bool CV07_Busy;              // DB1.DBX24.3
    public short CV07_Speed;            // DB1.DBW26

    // === 站 8 (byte 28~31) ===
    public bool CV08_DriveReady;        // DB1.DBX28.0
    public bool CV08_PalletArrived;     // DB1.DBX28.1
    public bool CV08_Fault;             // DB1.DBX28.2
    public bool CV08_Busy;              // DB1.DBX28.3
    public short CV08_Speed;            // DB1.DBW30

    // === 站 9 (byte 32~35) ===
    public bool CV09_DriveReady;        // DB1.DBX32.0
    public bool CV09_PalletArrived;     // DB1.DBX32.1
    public bool CV09_Fault;             // DB1.DBX32.2
    public bool CV09_Busy;              // DB1.DBX32.3
    public short CV09_Speed;            // DB1.DBW34

    // === 站 10 (byte 36~39) ===
    public bool CV10_DriveReady;        // DB1.DBX36.0
    public bool CV10_PalletArrived;     // DB1.DBX36.1
    public bool CV10_Fault;             // DB1.DBX36.2
    public bool CV10_Busy;              // DB1.DBX36.3
    public short CV10_Speed;            // DB1.DBW38
}

// ===================== DB2: 输送线任务请求 =====================

public struct PLC1_DB2_ConveyorRequest
{
    public bool CV01_RequestOut;        // DB2.DBX0.0  上升沿→请求出站
    public bool CV01_RequestIn;         // DB2.DBX0.1  上升沿→请求进站
    public byte CV01_TargetStation;     // DB2.DBB1    目标站号

    public bool CV02_RequestOut;        // DB2.DBX2.0
    public bool CV02_RequestIn;         // DB2.DBX2.1
    public byte CV02_TargetStation;     // DB2.DBB3

    public bool CV03_RequestOut;        // DB2.DBX4.0
    public bool CV03_RequestIn;         // DB2.DBX4.1
    public byte CV03_TargetStation;     // DB2.DBB5

    public bool CV04_RequestOut;        // DB2.DBX6.0
    public bool CV04_RequestIn;         // DB2.DBX6.1
    public byte CV04_TargetStation;     // DB2.DBB7

    public bool CV05_RequestOut;        // DB2.DBX8.0
    public bool CV05_RequestIn;         // DB2.DBX8.1
    public byte CV05_TargetStation;     // DB2.DBB9

    public bool CV06_RequestOut;        // DB2.DBX10.0
    public bool CV06_RequestIn;         // DB2.DBX10.1
    public byte CV06_TargetStation;     // DB2.DBB11

    public bool CV07_RequestOut;        // DB2.DBX12.0
    public bool CV07_RequestIn;         // DB2.DBX12.1
    public byte CV07_TargetStation;     // DB2.DBB13

    public bool CV08_RequestOut;        // DB2.DBX14.0
    public bool CV08_RequestIn;         // DB2.DBX14.1
    public byte CV08_TargetStation;     // DB2.DBB15

    public bool CV09_RequestOut;        // DB2.DBX16.0
    public bool CV09_RequestIn;         // DB2.DBX16.1
    public byte CV09_TargetStation;     // DB2.DBB17

    public bool CV10_RequestOut;        // DB2.DBX18.0
    public bool CV10_RequestIn;         // DB2.DBX18.1
    public byte CV10_TargetStation;     // DB2.DBB19
}

// ===================== DB3: 输送线报警 =====================

public struct PLC1_DB3_ConveyorAlarm
{
    public bool CV01_Alarm;             // DB3.DBX0.0
    public byte CV01_AlarmCode;         // DB3.DBB1
    public bool CV02_Alarm;             // DB3.DBX2.0
    public byte CV02_AlarmCode;         // DB3.DBB3
    public bool CV03_Alarm;             // DB3.DBX4.0
    public byte CV03_AlarmCode;         // DB3.DBB5
    public bool CV04_Alarm;             // DB3.DBX6.0
    public byte CV04_AlarmCode;         // DB3.DBB7
    public bool CV05_Alarm;             // DB3.DBX8.0
    public byte CV05_AlarmCode;         // DB3.DBB9
    public bool CV06_Alarm;             // DB3.DBX10.0
    public byte CV06_AlarmCode;         // DB3.DBB11
    public bool CV07_Alarm;             // DB3.DBX12.0
    public byte CV07_AlarmCode;         // DB3.DBB13
    public bool CV08_Alarm;             // DB3.DBX14.0
    public byte CV08_AlarmCode;         // DB3.DBB15
    public bool CV09_Alarm;             // DB3.DBX16.0
    public byte CV09_AlarmCode;         // DB3.DBB17
    public bool CV10_Alarm;             // DB3.DBX18.0
    public byte CV10_AlarmCode;         // DB3.DBB19
}

// ===================== 写命令: 输送线控制 =====================

[PlcBlock("PLC1", 101)]
public struct ConveyorControlCommand
{
    [PlcOffset(0, 0)] public bool StartStation1;    // DB101.DBX0.0
    [PlcOffset(0, 1)] public bool StopStation1;     // DB101.DBX0.1
    [PlcOffset(0, 2)] public bool ResetStation1;    // DB101.DBX0.2
    [PlcOffset(2)]    public short SpeedSetpoint1;   // DB101.DBW2

    [PlcOffset(4, 0)] public bool StartStation2;
    [PlcOffset(4, 1)] public bool StopStation2;
    [PlcOffset(4, 2)] public bool ResetStation2;
    [PlcOffset(6)]    public short SpeedSetpoint2;

    [PlcOffset(8, 0)]  public bool StartStation3;
    [PlcOffset(8, 1)]  public bool StopStation3;
    [PlcOffset(8, 2)]  public bool ResetStation3;
    [PlcOffset(10)]   public short SpeedSetpoint3;
    // ... 更多站位可继续加
}
