namespace Wcs.Core.PlcSubsystem.Examples;

// ====================================================================
// PLC2 — 堆垛机控制系统
// PLC 名称: "PLC2" | 地址: 192.168.0.2 | Rack:0 Slot:1
//
// DB1: 堆垛机状态 (byte[0..23], 4 台 × ~6 字节/台)
// DB2: 堆垛机任务请求 (byte[0..23], 4 台 × ~6 字节/台)
// DB3: 堆垛机报警 (byte[0..11], 4 台 × ~3 字节/台)
// DB201: 堆垛机控制命令 (写)
// ====================================================================

// ===================== DB1: 堆垛机状态 =====================

public struct PLC2_DB1_StackerStatus
{
    // === 1 号堆垛机 (byte 0~5) ===
    public bool ASRS01_Busy;            // DB1.DBX0.0
    public bool ASRS01_Fault;           // DB1.DBX0.1
    public bool ASRS01_AutoMode;        // DB1.DBX0.2
    public bool ASRS01_PositionArrived; // DB1.DBX0.3
    public short ASRS01_CurColumn;      // DB1.DBW2
    public short ASRS01_CurRow;         // DB1.DBW4

    // === 2 号堆垛机 (byte 6~11) ===
    public bool ASRS02_Busy;            // DB1.DBX6.0
    public bool ASRS02_Fault;           // DB1.DBX6.1
    public bool ASRS02_AutoMode;        // DB1.DBX6.2
    public bool ASRS02_PositionArrived; // DB1.DBX6.3
    public short ASRS02_CurColumn;      // DB1.DBW8
    public short ASRS02_CurRow;         // DB1.DBW10

    // === 3 号堆垛机 (byte 12~17) ===
    public bool ASRS03_Busy;            // DB1.DBX12.0
    public bool ASRS03_Fault;           // DB1.DBX12.1
    public bool ASRS03_AutoMode;        // DB1.DBX12.2
    public bool ASRS03_PositionArrived; // DB1.DBX12.3
    public short ASRS03_CurColumn;      // DB1.DBW14
    public short ASRS03_CurRow;         // DB1.DBW16

    // === 4 号堆垛机 (byte 18~23) ===
    public bool ASRS04_Busy;            // DB1.DBX18.0
    public bool ASRS04_Fault;           // DB1.DBX18.1
    public bool ASRS04_AutoMode;        // DB1.DBX18.2
    public bool ASRS04_PositionArrived; // DB1.DBX18.3
    public short ASRS04_CurColumn;      // DB1.DBW20
    public short ASRS04_CurRow;         // DB1.DBW22
}

// ===================== DB2: 堆垛机任务请求 =====================

public struct PLC2_DB2_StackerRequest
{
    public bool ASRS01_StoreReq;        // DB2.DBX0.0  上升沿→入库请求
    public bool ASRS01_RetrieveReq;     // DB2.DBX0.1  上升沿→出库请求
    public short ASRS01_TargetColumn;   // DB2.DBW2    目标列
    public short ASRS01_TargetRow;      // DB2.DBW4    目标行

    public bool ASRS02_StoreReq;        // DB2.DBX6.0
    public bool ASRS02_RetrieveReq;     // DB2.DBX6.1
    public short ASRS02_TargetColumn;   // DB2.DBW8
    public short ASRS02_TargetRow;      // DB2.DBW10

    public bool ASRS03_StoreReq;        // DB2.DBX12.0
    public bool ASRS03_RetrieveReq;     // DB2.DBX12.1
    public short ASRS03_TargetColumn;   // DB2.DBW14
    public short ASRS03_TargetRow;      // DB2.DBW16

    public bool ASRS04_StoreReq;        // DB2.DBX18.0
    public bool ASRS04_RetrieveReq;     // DB2.DBX18.1
    public short ASRS04_TargetColumn;   // DB2.DBW20
    public short ASRS04_TargetRow;      // DB2.DBW22
}

// ===================== DB3: 堆垛机报警 =====================

public struct PLC2_DB3_StackerAlarm
{
    public bool ASRS01_Alarm;           // DB3.DBX0.0
    public byte ASRS01_AlarmCode;       // DB3.DBB1
    public short ASRS01_FaultDetail;    // DB3.DBW2

    public bool ASRS02_Alarm;           // DB3.DBX4.0
    public byte ASRS02_AlarmCode;       // DB3.DBB5
    public short ASRS02_FaultDetail;    // DB3.DBW6

    public bool ASRS03_Alarm;           // DB3.DBX8.0
    public byte ASRS03_AlarmCode;       // DB3.DBB9
    public short ASRS03_FaultDetail;    // DB3.DBW10

    public bool ASRS04_Alarm;           // DB3.DBX12.0
    public byte ASRS04_AlarmCode;       // DB3.DBB13
    public short ASRS04_FaultDetail;    // DB3.DBW14
}

// ===================== 写命令: 堆垛机控制 =====================

[PlcBlock("PLC2", 201)]
public struct StackerControlCommand
{
    [PlcOffset(0, 0)] public bool StoreCmd1;     // DB201.DBX0.0
    [PlcOffset(0, 1)] public bool RetrieveCmd1;  // DB201.DBX0.1
    [PlcOffset(0, 2)] public bool ResetCmd1;      // DB201.DBX0.2
    [PlcOffset(2)]    public short TargetCol1;    // DB201.DBW2
    [PlcOffset(4)]    public short TargetRow1;    // DB201.DBW4

    [PlcOffset(6, 0)]  public bool StoreCmd2;
    [PlcOffset(6, 1)]  public bool RetrieveCmd2;
    [PlcOffset(6, 2)]  public bool ResetCmd2;
    [PlcOffset(8)]     public short TargetCol2;
    [PlcOffset(10)]    public short TargetRow2;
}
