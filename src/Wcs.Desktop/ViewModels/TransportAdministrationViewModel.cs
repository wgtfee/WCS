using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

/// <summary>
/// 第六阶段配置、审批、审计和运行日志只读监控页面。
/// 危险操作必须在接入认证和权限声明的 Host 客户端中执行，Desktop 默认不提供绕过审批的按钮。
/// </summary>
public partial class TransportAdministrationViewModel : ViewModelBase
{
    private readonly IWcsApiService _api;

    public ObservableCollection<TransportGovernedOperation> Operations { get; } = new();
    public ObservableCollection<TransportAuditRecord> Audits { get; } = new();
    public ObservableCollection<TransportJournalRecord> Journal { get; } = new();
    public ObservableCollection<TransportDriverEndpointDefinition> Drivers { get; } = new();

    [ObservableProperty] private long _configurationVersion;
    [ObservableProperty] private int _trafficResourceCount;
    [ObservableProperty] private int _chargingStationCount;
    [ObservableProperty] private int _vehicleDefinitionCount;
    [ObservableProperty] private int _driverDefinitionCount;
    [ObservableProperty] private int _pendingApprovalCount;
    [ObservableProperty] private int _failedOperationCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportAdministrationViewModel(IWcsApiService api)
    {
        _api = api;
    }

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在读取调度配置和审计数据...";
        try
        {
            var configurationTask = _api.GetTransportConfigurationAsync();
            var operationsTask = _api.GetTransportGovernedOperationsAsync();
            var auditsTask = _api.GetTransportAuditsAsync();
            var journalTask = _api.GetTransportJournalAsync();
            await Task.WhenAll(configurationTask, operationsTask, auditsTask, journalTask);

            var configuration = configurationTask.Result ?? new TransportRuntimeConfiguration();
            ConfigurationVersion = configuration.Version;
            TrafficResourceCount = configuration.TrafficResources.Count;
            ChargingStationCount = configuration.ChargingStations.Count;
            VehicleDefinitionCount = configuration.Vehicles.Count;
            DriverDefinitionCount = configuration.Drivers.Count;

            Replace(Drivers, configuration.Drivers);
            Replace(Operations, operationsTask.Result);
            Replace(Audits, auditsTask.Result);
            Replace(Journal, journalTask.Result);

            PendingApprovalCount = Operations.Count(x => x.State == TransportGovernedOperationState.PendingApproval);
            FailedOperationCount = Operations.Count(x => x.State == TransportGovernedOperationState.Failed);
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
