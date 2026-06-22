using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.PlcSubsystem.Modbus;
using Wcs.Core.PlcSubsystem.OpcUa;
using Wcs.Core.PlcSubsystem.Eip;
using Wcs.Core.PlcSubsystem.Mqtt;

namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// PLC 连接工厂 — 根据协议类型创建对应连接实例
/// </summary>
public class PlcConnectionFactory
{
    private readonly IServiceProvider _serviceProvider;

    public PlcConnectionFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>根据协议配置创建连接实例</summary>
    public IPlcConnection Create(ProtocolConnectionConfig config)
    {
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        return config.Protocol switch
        {
            PlcProtocolType.Modbus => new ModbusConnection(
                new ModbusConnectionConfig
                {
                    Name = config.Name,
                    Host = config.Host,
                    Port = config.Port,
                    TimeoutMs = config.TimeoutMs,
                },
                loggerFactory.CreateLogger<ModbusConnection>()),

            PlcProtocolType.OpcUa => new OpcUaConnection(
                new OpcUaConnectionConfig
                {
                    Name = config.Name,
                    EndpointUrl = $"opc.tcp://{config.Host}:{config.Port}",
                    TimeoutMs = config.TimeoutMs,
                },
                loggerFactory.CreateLogger<OpcUaConnection>()),

            PlcProtocolType.EIP => new EipConnection(
                new EipConnectionConfig
                {
                    Name = config.Name,
                    Host = config.Host,
                    TimeoutMs = config.TimeoutMs,
                },
                loggerFactory.CreateLogger<EipConnection>()),

            PlcProtocolType.Mqtt => new MqttConnection(
                new MqttConnectionConfig
                {
                    Name = config.Name,
                    Host = config.Host,
                    Port = config.Port,
                    TimeoutMs = config.TimeoutMs,
                },
                loggerFactory.CreateLogger<MqttConnection>()),

            _ => throw new NotSupportedException($"Protocol {config.Protocol} not supported")
        };
    }
}

/// <summary>通用协议连接配置</summary>
public class ProtocolConnectionConfig
{
    public string Name { get; set; } = "PLC";
    public PlcProtocolType Protocol { get; set; } = PlcProtocolType.Modbus;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public int TimeoutMs { get; set; } = 5000;
}
