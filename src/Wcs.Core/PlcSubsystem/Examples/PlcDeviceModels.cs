namespace Wcs.Core.PlcSubsystem.Examples;

// ============================================================
// 读状态结构体 — 定义 PLC DB 块的字段布局
// 字段顺序 = PLC 字节顺序，Struct.FromBytes 按字段顺序映射
// ============================================================

/// <summary>PLC1.DB1 — 输送线状态</summary>
public struct DB1_StatusBlock
{
    // byte 0 bits
    public bool CV01_DriveReady;        // DB1.DBX0.0
    public bool CV01_PalletArrived;     // DB1.DBX0.1
    public bool CV01_Fault;             // DB1.DBX0.2
    public bool CV02_DriveReady;        // DB1.DBX0.3
    public bool CV02_PalletArrived;     // DB1.DBX0.4
    public bool CV02_Fault;             // DB1.DBX0.5
    public bool CV03_DriveReady;        // DB1.DBX0.6
    public bool CV03_PalletArrived;     // DB1.DBX0.7
    // byte 1 bits
    public bool LIFT01_Idle;            // DB1.DBX1.0
    public bool LIFT01_AtTop;           // DB1.DBX1.1
    public bool LIFT01_Fault;           // DB1.DBX1.2
    // byte 2~5
    public short CV01_Speed;            // DB1.DBW2
    public short CV02_Speed;            // DB1.DBW4
}

/// <summary>PLC1.DB2 — 堆垛机状态</summary>
public struct DB2_MachineBlock
{
    public bool ASRS01_Busy;            // DB2.DBX0.0
    public bool ASRS01_Fault;           // DB2.DBX0.1
    public bool ASRS01_AutoMode;        // DB2.DBX0.2
    public short ASRS01_Position;       // DB2.DBW2
    public short ASRS01_TaskId;         // DB2.DBW4
}

// ============================================================
// 写命令结构体 — 标注目标 PLC + DB 块
// 写入时 PlcWriter 通过 [PlcBlock] 自动知道写到哪里
// [PlcOffset] 标注每个字段在 DB 块中的偏移
// ============================================================

/// <summary>输送线控制命令 → 写入 PLC1.DB101</summary>
[PlcBlock("PLC1", 101)]
public struct ConveyorCommand
{
    [PlcOffset(0, 0)] public bool Start;        // DB101.DBX0.0
    [PlcOffset(0, 1)] public bool Stop;         // DB101.DBX0.1
    [PlcOffset(0, 2)] public bool Reset;        // DB101.DBX0.2
    [PlcOffset(2)]    public short Speed;       // DB101.DBW2
}

/// <summary>提升机控制命令 → 写入 PLC1.DB102</summary>
[PlcBlock("PLC1", 102)]
public struct LiftCommand
{
    [PlcOffset(0, 0)] public bool GoUp;         // DB102.DBX0.0
    [PlcOffset(0, 1)] public bool GoDown;       // DB102.DBX0.1
    [PlcOffset(0, 2)] public bool Stop;         // DB102.DBX0.2
    [PlcOffset(2)]    public short TargetFloor;  // DB102.DBW2
}

/// <summary>堆垛机入库命令 → 写入 PLC2.DB201</summary>
[PlcBlock("PLC2", 201)]
public struct AsrsStoreCommand
{
    [PlcOffset(0, 0)] public bool StartStore;   // DB201.DBX0.0
    [PlcOffset(2)]    public short Column;       // DB201.DBW2
    [PlcOffset(4)]    public short Row;          // DB201.DBW4
    [PlcOffset(6)]    public short Depth;        // DB201.DBW6
}

// ============================================================
// PLC3 — 机器人/分拣线监控 PLC
// ============================================================

/// <summary>PLC3.DB1 — 机器人/分拣线状态</summary>
public struct PLC3_RobotBlock
{
    public bool ROBOT01_Busy;            // DB1.DBX0.0
    public bool ROBOT01_Gripped;         // DB1.DBX0.1
    public bool ROBOT01_Fault;           // DB1.DBX0.2
    public bool ROBOT01_PalletPresent;   // DB1.DBX0.3
    public bool SORTER01_Running;        // DB1.DBX0.4
    public bool SORTER01_Fault;          // DB1.DBX0.5
    public short ROBOT01_Speed;          // DB1.DBW2
    public short SORTER01_Count;         // DB1.DBW4
}

/// <summary>机器人控制命令 → 写入 PLC3.DB101</summary>
[PlcBlock("PLC3", 101)]
public struct RobotCommand
{
    [PlcOffset(0, 0)] public bool Grip;         // DB101.DBX0.0
    [PlcOffset(0, 1)] public bool Release;      // DB101.DBX0.1
    [PlcOffset(0, 2)] public bool StartMove;    // DB101.DBX0.2
    [PlcOffset(0, 3)] public bool Stop;         // DB101.DBX0.3
    [PlcOffset(2)]    public short TargetPos;   // DB101.DBW2
    [PlcOffset(4)]    public short Speed;       // DB101.DBW4
}

/// <summary>分拣线控制命令 → 写入 PLC3.DB102</summary>
[PlcBlock("PLC3", 102)]
public struct SorterCommand
{
    [PlcOffset(0, 0)] public bool Start;        // DB102.DBX0.0
    [PlcOffset(0, 1)] public bool Stop;         // DB102.DBX0.1
    [PlcOffset(0, 2)] public bool EmergencyStop;// DB102.DBX0.2
    [PlcOffset(2)]    public short SortTarget;  // DB102.DBW2
}
