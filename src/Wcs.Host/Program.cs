using Serilog;
using SqlSugar;
using Wcs.Application;
using Wcs.Core.AlarmCenter;
using Wcs.Core.Common.Options;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.Recovery;
using Wcs.Host.BackgroundServices;
using Wcs.Infrastructure;
using Wcs.Infrastructure.Logging;
using Wcs.Infrastructure.Persistence;
using Wcs.Infrastructure.SignalR;
using Microsoft.Extensions.Options;
using Wcs.Simulator;
using Wcs.Simulator.PlcSimulator;

Log.Logger = LoggingSetup.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog(Log.Logger, dispose: true);
    builder.Services.AddWcsApplication();
    builder.Services.AddWcsInfrastructure(builder.Configuration);
    builder.Services.Configure<WcsOptions>(builder.Configuration.GetSection("WcsOptions"));

    builder.Services.AddSingleton<IPlcBlockDiffEngine, PlcBlockDiffEngine>();

    // ===== 配置驱动 PLC 注册（必须始终执行，PlcWriter/PlcStructRegistry 依赖）=====
    builder.Services.AddWcsPlc(builder.Configuration);

    // ===== 虚拟工厂 / 真实 PLC 切换 =====
    var simulatorEnabled = builder.Configuration.GetSection("Simulator").GetValue<bool>("Enabled");

    if (simulatorEnabled)
    {
        builder.Services.AddSingleton<SimulatorSignalSource>();
        builder.Services.AddSingleton<ISignalSource>(sp => sp.GetRequiredService<SimulatorSignalSource>());
        builder.Services.AddSingleton<VirtualPlant>(sp =>
        {
            var gen = new TransportGenerator(
                sp.GetRequiredService<Wcs.Core.TaskEngine.Scheduler.ITaskScheduler>(),
                sp.GetRequiredService<ILogger<TransportGenerator>>());
            var plant = new VirtualPlant(gen, sp.GetRequiredService<ILogger<VirtualPlant>>());
            plant.BuildDefaultTopology();
            return plant;
        });
        builder.Services.AddSingleton<SimulatorOrchestrator>(sp =>
            new SimulatorOrchestrator(
                sp.GetRequiredService<VirtualPlant>(),
                sp.GetRequiredService<Wcs.Core.TaskEngine.Scheduler.ITaskScheduler>(),
                sp.GetRequiredService<Wcs.Core.EventBus.Publisher.IEventBus>(),
                sp.GetRequiredService<Wcs.Core.StateCenter.Interfaces.IStateCenter>(),
                sp.GetRequiredService<ILogger<SimulatorOrchestrator>>()));
        Log.Logger.Information("🧪 虚拟工厂模式已启用");
        builder.Services.AddHostedService<SimulatorBackgroundService>();
    }
    else
    {
        Log.Logger.Information("🏭 真实 PLC 模式已启用");
        builder.Services.AddHostedService<S7PollingBackgroundService>();
    }

    builder.Services.AddHostedService<SnapshotBackgroundService>();
    builder.Services.AddHostedService<PersistBackgroundService>();
    builder.Services.AddHostedService<AlarmMonitorBackgroundService>();

    builder.Services.AddWindowsService(options => options.ServiceName = "WCS Runtime Engine");
    builder.Services.AddSignalR();
    builder.Services.AddControllers();
    builder.Services.AddHealthChecks()
        .AddCheck<Wcs.Host.HealthChecks.WcsReadinessCheck>("readiness")
        .AddCheck<Wcs.Host.HealthChecks.WcsLivenessCheck>("liveness");

    var app = builder.Build();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("WCS Runtime Engine 启动中...");

    try
    {
        var dbInit = app.Services.GetRequiredService<IDatabaseInitializer>();
        await dbInit.EnsureDatabaseAsync();
        logger.LogInformation("数据库就绪");

        try
        {
            var connStr = builder.Configuration.GetConnectionString("WcsDb");
            if (!string.IsNullOrEmpty(connStr))
            {
                using var sugarDb = new SqlSugarClient(new ConnectionConfig
                {
                    ConnectionString = connStr,
                    DbType = DbType.SqlServer,
                    IsAutoCloseConnection = true
                });
                sugarDb.CodeFirst.InitTables(
                    typeof(TaskRunEntity),
                    typeof(TransportHistoryEntity),
                    typeof(CommandLogEntity),
                    typeof(DeviceStateLogEntity));
                logger.LogInformation("WCS 业务表已就绪");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SqlSugar 表初始化失败");
        }
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "数据库初始化失败，系统无法启动");
        throw;
    }

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

    try
    {
        var alarmCenter = app.Services.GetRequiredService<IAlarmCenter>();
        var wcsOptions = app.Services.GetRequiredService<IOptions<WcsOptions>>();
        foreach (var rule in wcsOptions.Value.AlarmRules)
            alarmCenter.SetAlarmRule(rule);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "加载报警规则失败");
    }

    app.MapHub<WcsHub>("/wcs");
    app.MapControllers();
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
