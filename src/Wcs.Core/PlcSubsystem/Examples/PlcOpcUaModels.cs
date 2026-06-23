// ====================================================================
// OPC UA 示例模型
//
// 读取链路：OpcUaPollingService → OpcUaTagSerializer → OpcUaPlcClient
//   → OpcUaConnection.ReadAsync("ns=2;s=...", 1) → OPC UA Read
//
// Json 配置：
//   "PlcOpcUaPolls": [
//     { "StructType": "Wcs.Core.PlcSubsystem.Examples.OpcUaConveyorStatus, Wcs.Core" }
//   ]
// ====================================================================

namespace Wcs.Core.PlcSubsystem.Examples;

/// <summary>
/// OPC UA 输送线状态 — 每个属性对应一个 OPC UA 节点
/// </summary>
[PlcOpcUaBlock]
public class OpcUaConveyorStatus
{
    [PlcOpcUaTag("ns=2;s=Station1.Status.DriveReady")]
    public bool DriveReady { get; set; }

    [PlcOpcUaTag("ns=2;s=Station1.Status.PalletArrived")]
    public bool PalletArrived { get; set; }

    [PlcOpcUaTag("ns=2;s=Station1.Status.Fault")]
    public bool Fault { get; set; }

    [PlcOpcUaTag("ns=2;s=Station1.Status.Speed")]
    public short Speed { get; set; }

    [PlcOpcUaTag("ns=2;s=Station1.Status.Temperature")]
    public float Temperature { get; set; }

    [PlcOpcUaTag("ns=2;s=Station1.Status.RunHours")]
    public int RunHours { get; set; }
}

/// <summary>
/// OPC UA 控制命令
/// </summary>
[PlcOpcUaBlock]
public class OpcUaControlCommand
{
    [PlcOpcUaTag("ns=2;s=Station1.Command.Start")]
    public bool Start { get; set; }

    [PlcOpcUaTag("ns=2;s=Station1.Command.Stop")]
    public bool Stop { get; set; }

    [PlcOpcUaTag("ns=2;s=Station1.Command.SpeedSetpoint")]
    public short SpeedSetpoint { get; set; }
}

/// <summary>
/// OPC UA 环境监测
/// </summary>
[PlcOpcUaBlock]
public class OpcUaEnvironmentData
{
    [PlcOpcUaTag("ns=2;s=Environment.Temperature")]
    public float Temperature { get; set; }

    [PlcOpcUaTag("ns=2;s=Environment.Humidity")]
    public float Humidity { get; set; }

    [PlcOpcUaTag("ns=2;s=Environment.Pressure")]
    public float Pressure { get; set; }
}
