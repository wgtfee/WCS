using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.PlcSubsystem.Label;
using Wcs.Core.PlcSubsystem.Modbus;
using Wcs.Core.PlcSubsystem.OpcUa;
using Wcs.Core.PlcSubsystem.Eip;
using Wcs.Core.PlcSubsystem.Mqtt;
using Wcs.Core.PlcSubsystem.S7.S7CommPlus;

namespace Wcs.Core.PlcSubsystem;

/// <summary>
/// PLC 子系统 DI 注册扩展
/// </summary>
public static class PlcRegistrationExtensions
{
    /// <summary>
    /// 注册 PLC 子系统核心服务
    /// </summary>
    public static IServiceCollection AddWcsPlcCore(this IServiceCollection services)
    {
        // 工厂
        services.AddSingleton<PlcConnectionFactory>();

        // 协议连接类型注册（可被工厂创建，也支持直接 DI 注入）
        services.TryAddTransient<ModbusConnection>();
        services.TryAddTransient<OpcUaConnection>();
        services.TryAddTransient<EipConnection>();
        services.TryAddTransient<MqttConnection>();

        // 标签式读写
        services.AddSingleton<PlcTagRegistry>();
        services.TryAddTransient<PlcTagSerializer>();

        return services;
    }

    /// <summary>
    /// 注册具体的 PLC 连接实例（用于 appsettings.json 配置多 PLC）
    /// </summary>
    public static IServiceCollection AddPlcConnection(this IServiceCollection services,
        ProtocolConnectionConfig config)
    {
        services.AddSingleton<IPlcConnection>(sp =>
        {
            var factory = sp.GetRequiredService<PlcConnectionFactory>();
            return factory.Create(config);
        });

        return services;
    }

    /// <summary>
    /// 使用 S7CommPlus 符号标签协议（适用于 S7-1500）
    /// 从 appsettings.json 读取连接配置（S7CommPlus 节）
    /// </summary>
    public static IServiceCollection AddS7CommPlus(this IServiceCollection services,
        S7CommPlusConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        services.AddSingleton(config);
        services.TryAddTransient<IPlcClient, S7CommPlusPlcClient>();
        return services;
    }
}
