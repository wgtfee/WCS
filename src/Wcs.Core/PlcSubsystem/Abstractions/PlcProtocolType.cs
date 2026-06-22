namespace Wcs.Core.PlcSubsystem.Abstractions;

/// <summary>支持的 PLC 协议类型</summary>
public enum PlcProtocolType
{
    /// <summary>Siemens S7 (Snap7)</summary>
    S7,

    /// <summary>Modbus TCP / RTU</summary>
    Modbus,

    /// <summary>OPC UA</summary>
    OpcUa,

    /// <summary>EtherNet/IP (CIP)</summary>
    EIP,

    /// <summary>MQTT (物联网消息)</summary>
    Mqtt,
}
