using Microsoft.Extensions.DependencyInjection;
using Wcs.Desktop.Interface;
using Microsoft.Extensions.Configuration;
using Wcs.Desktop.Services;
using Wcs.Desktop.ViewModels;

namespace Wcs.Desktop;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWcsDesktop(this IServiceCollection services)
    {
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false).Build();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<WcsDesktopOptions>(config.GetSection("WcsDesktop"));
        services.AddHttpClient<IWcsApiService, WcsApiService>();
        services.AddHttpClient<ITransportResilienceApiService, TransportResilienceApiService>();
        services.AddHttpClient<ITransportSimulationApiService, TransportSimulationApiService>();
        services.AddHttpClient<ISimulationVerificationApiService, SimulationVerificationApiService>();
        services.AddHttpClient<IModelOpsApiService, ModelOpsApiService>();
        services.AddHttpClient<IMaintenanceLearningApiService, MaintenanceLearningApiService>();
        services.AddSingleton<IWcsRealtimeService, WcsRealtimeService>();
        services.AddSingleton<IDataProvider, ApiDataProvider>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<DevicesViewModel>();
        services.AddTransient<TasksViewModel>();
        services.AddTransient<AlarmsViewModel>();
        services.AddTransient<ObjectsViewModel>();
        services.AddTransient<EventLogViewModel>();
        services.AddTransient<TrackingLogViewModel>();
        services.AddTransient<AuditLogViewModel>();
        services.AddTransient<SysLogViewModel>();
        services.AddTransient<TransportSchedulingViewModel>();
        services.AddTransient<TransportTrafficViewModel>();
        services.AddTransient<TransportOptimizationViewModel>();
        services.AddTransient<TransportAdministrationViewModel>();
        services.AddTransient<TransportDriverDiagnosticsViewModel>();
        services.AddTransient<TransportCommissioningViewModel>();
        services.AddTransient<TransportProductionViewModel>();
        services.AddTransient<TransportObservabilityViewModel>();
        services.AddTransient<TransportResilienceViewModel>();
        services.AddTransient<TransportSimulationViewModel>();
        services.AddTransient<SimulationVerificationViewModel>();
        services.AddTransient<ModelOpsViewModel>();
        services.AddTransient<MaintenanceLearningViewModel>();
        return services;
    }
}
