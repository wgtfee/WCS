using Serilog;
using Wcs.Application;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.Recovery;
using Wcs.Host.BackgroundServices;
using Wcs.Infrastructure;
using Wcs.Infrastructure.Logging;
using Wcs.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

// 配置 Serilog
Log.Logger = LoggingSetup.CreateLogger();
builder.Services.AddSerilog(Log.Logger, dispose: true);

// 注册 WCS 应用层 (含所有 Core 服务)
builder.Services.AddWcsApplication();

// 注册基础设施层 (数据库、Dapper 仓库等)
builder.Services.AddWcsInfrastructure(builder.Configuration);

// 注册 PLC 子系统
builder.Services.AddSingleton<IPlcBlockDiffEngine, PlcBlockDiffEngine>();
builder.Services.AddSingleton<IPlcPollingService>(sp =>
    new PlcPollingService(sp.GetRequiredService<ILogger<PlcPollingService>>()));

// 注册后台服务
builder.Services.AddHostedService<PlcPollingBackgroundService>();
builder.Services.AddHostedService<SnapshotBackgroundService>();
builder.Services.AddHostedService<PersistBackgroundService>();
builder.Services.AddHostedService<AlarmMonitorBackgroundService>();

// Windows Service 支持
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "WCS Runtime Engine";
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("WCS Runtime Engine 启动中...");

// 数据库初始化 (在后台服务启动前执行)
try
{
    var dbInit = host.Services.GetRequiredService<IDatabaseInitializer>();
    await dbInit.EnsureDatabaseAsync();
    logger.LogInformation("数据库就绪");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "数据库初始化失败，系统无法启动");
    throw;
}

// 执行系统恢复
try
{
    var recovery = host.Services.GetRequiredService<IRecoveryManager>();
    if (await recovery.NeedsRecoveryAsync())
    {
        var result = await recovery.RecoverAsync();
        logger.LogInformation("系统恢复: {Message} (设备={Devices}, 任务={Tasks})",
            result.Message, result.RestoredDevices, result.RestoredTasks);
    }
}
catch (Exception ex)
{
    logger.LogWarning(ex, "系统恢复失败，以全新状态启动");
}

await host.RunAsync();
Log.CloseAndFlush();
