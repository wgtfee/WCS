// ====================================================================
// Modbus TCP 示例模型
//
// 读取链路：ModbusPollingService → ModbusTagSerializer → ModbusPlcClient
//   → ModbusConnection.ReadAsync("HR:0", 1) → NModbus ReadHoldingRegistersAsync
//
// Json 配置：
//   "PlcModbusPolls": [
//     { "StructType": "Wcs.Core.PlcSubsystem.Examples.ModbusConveyorStatus, Wcs.Core" }
//   ]
// ====================================================================

namespace Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// Modbus 输送线状态 — 从保持寄存器（HR）读取
/// 占用: HR0~HR4 (5 registers × 2 bytes = 10 bytes)
/// </summary>
[PlcModbusBlock("HR", UnitId = 1)]
public class ModbusConveyorStatus
{
    [PlcModbusTag(0)]          public short Speed { get; set; }        // HR0
    [PlcModbusTag(0, Bit = 0)] public bool DriveReady { get; set; }   // HR0.0
    [PlcModbusTag(0, Bit = 1)] public bool PalletArrived { get; set; }// HR0.1
    [PlcModbusTag(0, Bit = 2)] public bool Fault { get; set; }       // HR0.2
    [PlcModbusTag(1)]          public short Temperature { get; set; } // HR1
    [PlcModbusTag(2)]          public short Pressure { get; set; }    // HR2
    [PlcModbusTag(3)]          public short TargetSpeed { get; set; } // HR3
    [PlcModbusTag(4)]          public short RunHours { get; set; }   // HR4
}

/// <summary>
/// Modbus 控制命令 — 写入保持寄存器
/// </summary>
[PlcModbusBlock("HR", UnitId = 1)]
public class ModbusControlCommand
{
    [PlcModbusTag(0, Bit = 0)] public bool Start { get; set; }
    [PlcModbusTag(0, Bit = 1)] public bool Stop { get; set; }
    [PlcModbusTag(0, Bit = 2)] public bool Reset { get; set; }
    [PlcModbusTag(1)]          public short SpeedSetpoint { get; set; }
}
