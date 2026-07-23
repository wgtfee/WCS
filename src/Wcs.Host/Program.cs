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
using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem.Label;
using Wcs.Core.PlcSubsystem.Validation.Examples;
using Wcs.Core.PlcSubsystem.Modbus;
using Wcs.Core.PlcSubsystem.OpcUa;
using Wcs.Core.PlcSubsystem.S7;
using Wcs.Core.PlcSubsystem.S7.S7CommPlus;
using Wcs.Core.SignalSnapshot;
using Wcs.Core.TransportScheduling;
using Wcs.Host.Middleware;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

Log.Logger = LoggingSetup.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSerilog(Log.Logger, dispose: true);
    builder.Services.AddWcsApplication();
    builder.Services.AddWcsInfrastructure(builder.Configuration);
    builder.Services.Configure<WcsOptions>(builder.Configuration.GetSection("WcsOptions"));

    var transportObservability = builder.Configuration
        .GetSection("TransportObservability")
        .Get<TransportObservabilityOptions>() ?? new TransportObservabilityOptions();
    builder.Services.AddSingleton(transportObservability);

    Uri? otlpEndpoint = null;
    if (transportObservability.EnableOtlpExporter &&
        !string.IsNullOrWhiteSpace(transportObservability.OtlpEndpoint) &&
        Uri.TryCreate(transportObservability.OtlpEndpoint, UriKind.Absolute, out var configuredOtlpEndpoint))
    {
        otlpEndpoint = configuredOtlpEndpoint;
    }

    var openTelemetry = builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: TransportTelemetryNames.ServiceName,
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(tracing =>
        {
            tracing
                .AddSource(TransportTelemetryNames.ActivitySourceName)
                .AddAspNetCoreInstrumentation(options => options.RecordException = true)
                .AddHttpClientInstrumentation(options => options.RecordException = true);
            if (otlpEndpoint is not null)
                tracing.AddOtlpExporter(options => options.Endpoint = otlpEndpoint);
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddMeter(TransportTelemetryNames.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation();
            if (transportObservability.EnablePrometheusEndpoint)
                metrics.AddPrometheusExporter();
            if (otlpEndpoint is not null)
                metrics.AddOtlpExporter(options => options.Endpoint = otlpEndpoint);
        });

    builder.Services.AddSingleton<IPlcBlockDiffEngine, PlcBlockDiffEngine>();

    // ===== SqlSugar DI =====
    var dbConnStr = builder.Configuration.GetConnectionString("WcsDb");
    if (!string.IsNullOrEmpty(dbConnStr))
        builder.Services.AddSingleton<ISqlSugarClient>(_ => new SqlSugarScope(
            new ConnectionConfig
            {
                ConnectionString = dbConnStr + ";MultipleActiveResultSets=True;Max Pool Size=200",
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true
            }));

    // ===== PLC 核心注册 =====
    var connectToPlc = !builder.Configuration.GetSection("Simulator").GetValue<bool>("Enabled");
    builder.Services.AddWcsPlc(builder.Configuration, connectToPlc);

    // ===== 后台服务 =====
    if (connectToPlc)
    {
        //西门子PLC 轮询服务
        builder.Services.AddHostedService<S7PollingBackgroundService>();
        //偏移量地址读取服务
        builder.Services.AddWcsPlcCore();
        //标签读取服务
        var plusConfig = builder.Configuration.GetSection("S7CommPlus").Get<S7CommPlusConfig>();
        if (plusConfig == null)
            throw new InvalidOperationException("未找到 S7CommPlus 配置节");
        builder.Services.AddS7CommPlus(plusConfig);
        //标签轮询服务
        builder.Services.AddSingleton<TagPollingService>(sp =>
        {
            var serializer = sp.GetRequiredService<PlcTagSerializer>();
            var logger = sp.GetRequiredService<ILogger<TagPollingService>>();
            var snapshot = sp.GetService<SignalSnapshotCenter>();
            var detector = sp.GetService<EventDetector>();
            var service = new TagPollingService(serializer, logger, snapshot, detector);

            // 从 appsettings.json → PlcTagPolls 读取所有轮询类型
            var tagPolls = builder.Configuration.GetSection("PlcTagPolls").Get<TagPollConfig[]>();
            if (tagPolls != null && tagPolls.Length > 0)
            {
                service.AddFromConfig(tagPolls);
                logger.LogInformation("标签轮询: 从配置加载 {Count} 个类型", tagPolls.Length);
            }

            return service;
        });
        builder.Services.AddHostedService<TagPollingBackgroundService>();

        //Modbus 标签轮询（按需启用）
        var modbusConfig = builder.Configuration.GetSection("PlcModbusPolls").Get<TagPollConfig[]>();
        if (modbusConfig is { Length: > 0 })
        {
            builder.Services.AddModbus();
            builder.Services.AddSingleton<ModbusPollingService>(sp =>
            {
                var serializer = sp.GetRequiredService<ModbusTagSerializer>();
                var logger = sp.GetRequiredService<ILogger<ModbusPollingService>>();
                var snapshot = sp.GetService<SignalSnapshotCenter>();
                var detector = sp.GetService<EventDetector>();
                var service = new ModbusPollingService(serializer, logger, snapshot, detector);
                service.AddFromConfig(modbusConfig);
                return service;
            });
            builder.Services.AddHostedService<ModbusPollingBackgroundService>();
            Log.Logger.Information("Modbus 标签轮询: 加载 {Count} 个类型", modbusConfig.Length);
        }

        //OPC UA 标签轮询（按需启用）
        var opcuaConfig = builder.Configuration.GetSection("PlcOpcUaPolls").Get<TagPollConfig[]>();
        if (opcuaConfig is { Length: > 0 })
        {
            builder.Services.AddOpcUa();
            builder.Services.AddSingleton<OpcUaPollingService>(sp =>
            {
                var serializer = sp.GetRequiredService<OpcUaTagSerializer>();
                var logger = sp.GetRequiredService<ILogger<OpcUaPollingService>>();
                var snapshot = sp.GetService<SignalSnapshotCenter>();
                var detector = sp.GetService<EventDetector>();
                var service = new OpcUaPollingService(serializer, logger, snapshot, detector);
                service.AddFromConfig(opcuaConfig);
                return service;
            });
            builder.Services.AddHostedService<OpcUaPollingBackgroundService>();
            Log.Logger.Information("OPC UA 标签轮询: 加载 {Count} 个类型", opcuaConfig.Length);
        }

        // ===== 注册所有可用的 ITagSerializer 到 DI（给 CommandCenter 路由用） =====
        builder.Services.AddSingleton<ITagSerializer>(sp =>
            new Snap7TagSerializer(sp.GetRequiredService<PlcWriter>()));
        builder.Services.AddSingleton<ITagSerializer>(sp =>
            new PlcTagSerializer(sp.GetRequiredService<IPlcClient>()));

        // 按需注册具体的 PLC 连接
        //builder.Services.AddPlcConnection(new ProtocolConnectionConfig
        //{
        //    Name = "ModbusPLC1",
        //    Protocol = PlcProtocolType.Modbus,
        //    Host = "192.168.1.100",
        //    Port = 502,
        //});
        Log.Logger.Information("🏭 真实 PLC 模式");
    }
    else
    {
        builder.Services.AddSingleton(sp => new SimulatedPlcPollingService(
            sp.GetRequiredService<PlcStructRegistry>(),
            sp.GetRequiredService<Wcs.Core.StateCenter.Interfaces.IStateCenter>(),
            sp.GetRequiredService<EventDetector>(),
            sp.GetRequiredService<SignalSnapshotCenter>(),
            sp.GetRequiredService<ILogger<SimulatedPlcPollingService>>()));
        builder.Services.AddHostedService<SimulatorBackgroundService>();
        Log.Logger.Information("🧪 模拟模式 — 3 PLC 9 DB + 18 验证器");
    }

    builder.Services.AddHostedService<SnapshotBackgroundService>();
    builder.Services.AddHostedService<PersistBackgroundService>();
    builder.Services.AddHostedService<AlarmMonitorBackgroundService>();
    builder.Services.AddHostedService<EventPersistenceService>();
    builder.Services.AddHostedService<PlcTelemetryEventBridgeService>();
    builder.Services.AddHostedService<PlcAnomalyDetectionService>();
    builder.Services.AddHostedService<PlcAnomalyPersistenceService>();
    builder.Services.AddHostedService<PlcAnomalyAlarmBridgeService>();
    builder.Services.AddHostedService<TaskGeneratorService>();
    builder.Services.AddHostedService<TaskExecutionWorker>();
    builder.Services.AddHostedService<AlarmWiringService>();
    builder.Services.AddHostedService<SignalResponseService>();

    builder.Services.AddWindowsService(options => options.ServiceName = "WCS Runtime Engine");
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<SignalRStatePublisher>();
    builder.Services.AddHostedService<SignalRBridgeBackgroundService>();
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

    RegisterPlcValidators(app.Services, logger);

    app.UseMiddleware<TransportTraceContextMiddleware>();
    if (transportObservability.EnablePrometheusEndpoint)
        app.UseOpenTelemetryPrometheusScrapingEndpoint();

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

/// <summary>注册所有 PLC 验证器</summary>
static void RegisterPlcValidators(IServiceProvider services, Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        var detector = services.GetRequiredService<EventDetector>();

        // === Snap7 struct 验证器 ===
        // detector.RegisterValidator(new StationInterlockValidator());

        // === 标签验证器 ===
        // detector.RegisterValidator(new TagStationInterlockValidator());
        // detector.RegisterValidator(new TagBarcodeDbValidator());

        // === Modbus / OPC UA ===
        // detector.RegisterValidator(new ModbusConveyorValidator());

        logger.LogInformation("PLC 验证器注册完成，共 2 个");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "PLC 验证器注册失败");
    }
}
