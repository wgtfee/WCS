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
using Wcs.Simulator.PlcSimulatorEngine;

Log.Logger = LoggingSetup.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog(Log.Logger, dispose: true);
    builder.Services.AddWcsApplication();
    builder.Services.AddWcsInfrastructure(builder.Configuration);
    builder.Services.Configure<WcsOptions>(builder.Configuration.GetSection("WcsOptions"));

    builder.Services.AddSingleton<IPlcBlockDiffEngine, PlcBlockDiffEngine>();

    // ===== SqlSugar DI =====
    var dbConnStr = builder.Configuration.GetConnectionString("WcsDb");
    if (!string.IsNullOrEmpty(dbConnStr))
        builder.Services.AddSingleton<ISqlSugarClient>(_ => new SqlSugarClient(
            new ConnectionConfig { ConnectionString = dbConnStr + ";MultipleActiveResultSets=True;Max Pool Size=200", DbType = DbType.SqlServer, IsAutoCloseConnection = false }));

    // ===== PLC 核心注册 =====
    var connectToPlc = !builder.Configuration.GetSection("Simulator").GetValue<bool>("Enabled");
    builder.Services.AddWcsPlc(builder.Configuration, connectToPlc);

    // ===== 后台服务 =====
    if (connectToPlc)
    {
        builder.Services.AddHostedService<S7PollingBackgroundService>();
        Log.Logger.Information("🏭 真实 PLC 模式");
    }
    else
    {
        builder.Services.AddSingleton(sp => new SimulatedPlcPollingService(
            sp.GetRequiredService<Wcs.Core.PlcSubsystem.S7.PlcStructRegistry>(),
            sp.GetRequiredService<Wcs.Core.StateCenter.Interfaces.IStateCenter>(),
            sp.GetRequiredService<Wcs.Core.EventDetection.EventDetector>(),
            sp.GetRequiredService<Wcs.Core.SignalSnapshot.SignalSnapshotCenter>(),
            sp.GetRequiredService<ILogger<SimulatedPlcPollingService>>()));
        builder.Services.AddHostedService<SimulatorBackgroundService>();
        Log.Logger.Information("🧪 模拟模式 — 3 PLC 9 DB + 18 验证器");
    }

    builder.Services.AddHostedService<SnapshotBackgroundService>();
    builder.Services.AddHostedService<PersistBackgroundService>();
    builder.Services.AddHostedService<AlarmMonitorBackgroundService>();
    builder.Services.AddHostedService<EventPersistenceService>();
    builder.Services.AddHostedService<TaskGeneratorService>();
    builder.Services.AddHostedService<TaskExecutionWorker>();

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

    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "数据库初始化失败");
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
        logger.LogWarning(ex, "系统恢复失败");
    }

    try
    {
        var alarmCenter = app.Services.GetRequiredService<IAlarmCenter>();
        var wcsOptions = app.Services.GetRequiredService<IOptions<WcsOptions>>();
        foreach (var rule in wcsOptions.Value.AlarmRules)
            alarmCenter.SetAlarmRule(rule);
    }
    catch { }

    app.MapHub<WcsHub>("/wcs");
    app.MapControllers();
    app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Name == "readiness" });
    app.MapHealthChecks("/health/live", new() { Predicate = r => r.Name == "liveness" });
    app.MapHealthChecks("/health");
    app.MapGet("/", () => "WCS Runtime Engine is running.");

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
