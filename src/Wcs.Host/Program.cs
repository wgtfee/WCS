using Serilog;
using Wcs.Application;
using Wcs.Core.Common.Options;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.Recovery;
using Wcs.Host.BackgroundServices;
using Wcs.Infrastructure;
using Wcs.Infrastructure.Logging;
using Wcs.Infrastructure.Persistence;
using Wcs.Infrastructure.SignalR;

Log.Logger = LoggingSetup.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog(Log.Logger, dispose: true);

    // WCS 应用层
    builder.Services.AddWcsApplication();
    // 基础设施层
    builder.Services.AddWcsInfrastructure(builder.Configuration);
    // 配置热重载
    builder.Services.Configure<WcsOptions>(builder.Configuration.GetSection("WcsOptions"));

    // PLC 子系统
    builder.Services.AddSingleton<IPlcBlockDiffEngine, PlcBlockDiffEngine>();
    // PLC 真实驱动 - 从配置创建
    builder.Services.AddSingleton<Wcs.Infrastructure.S7.IS7ConnectionFactory>(sp =>
    {
        var configs = builder.Configuration.GetSection("PlcConnections")
            .Get<List<S7ConnectionConfig>>() ?? new();
        return new Wcs.Infrastructure.S7.S7ConnectionFactory(configs);
    });

    builder.Services.AddSingleton<IPlcPollingService>(sp =>
        new PlcPollingService(sp.GetRequiredService<ILogger<PlcPollingService>>()));

    // 后台服务
    builder.Services.AddHostedService<PlcPollingBackgroundService>();
    builder.Services.AddHostedService<SnapshotBackgroundService>();
    builder.Services.AddHostedService<PersistBackgroundService>();
    builder.Services.AddHostedService<AlarmMonitorBackgroundService>();

    // Windows Service
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "WCS Runtime Engine";
    });

    // SignalR 实时推送
    builder.Services.AddSignalR();
    // REST API 控制器
    builder.Services.AddControllers();
    // 健康检查
    builder.Services.AddHealthChecks()
        .AddCheck<Wcs.Host.HealthChecks.WcsReadinessCheck>("readiness")
        .AddCheck<Wcs.Host.HealthChecks.WcsLivenessCheck>("liveness");

    var app = builder.Build();

    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("WCS Runtime Engine 启动中...");

    // 数据库初始化
    try
    {
        var dbInit = app.Services.GetRequiredService<IDatabaseInitializer>();
        await dbInit.EnsureDatabaseAsync();
        logger.LogInformation("数据库就绪");
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "数据库初始化失败，系统无法启动");
        throw;
    }

    // 系统恢复
    try
    {
        var recovery = app.Services.GetRequiredService<IRecoveryManager>();
        if (await recovery.NeedsRecoveryAsync())
        {
            var result = await recovery.RecoverAsync();
            logger.LogInformation("系统恢复: {Message}", result.Message);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "系统恢复失败，以全新状态启动");
    }

    // SignalR Hub 端点
    app.MapHub<WcsHub>("/wcs");
    // REST API 控制器
    app.MapControllers();

    // 默认页面
        // 健康检查端点
    app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Name == "readiness" });
    app.MapHealthChecks("/health/live", new() { Predicate = r => r.Name == "liveness" });
    app.MapHealthChecks("/health");

    app.MapGet("/", () => "WCS Runtime Engine is running.");

    logger.LogInformation("SignalR hub available at /wcs");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
