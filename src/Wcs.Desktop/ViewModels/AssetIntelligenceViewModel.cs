namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;

public partial class AssetIntelligenceViewModel : ViewModelBase
{
    private readonly IAssetIntelligenceApiService _api;

    public ObservableCollection<AssetHealthRowDto> HealthAssets { get; } = [];
    public ObservableCollection<RootCauseRowDto> RootCauses { get; } = [];
    public ObservableCollection<MaintenanceRowDto> Maintenance { get; } = [];
    public ObservableCollection<ForecastRowDto> Forecasts { get; } = [];

    [ObservableProperty] private int _trackedAssetCount;
    [ObservableProperty] private int _degradedAssetCount;
    [ObservableProperty] private int _rootCauseCount;
    [ObservableProperty] private int _maintenanceCount;
    [ObservableProperty] private int _forecastCount;
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private bool _isBusy;

    public string BoundaryText => "智能运维中心统一展示 Health / Root Cause / Maintenance / Failure Forecast。它是诊断与维修决策支持入口，不提供 PLC 写入、设备停机、任务取消、路线/路权或 Dispatch 修改。";

    public AssetIntelligenceViewModel(IAssetIntelligenceApiService api) => _api = api;

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在汇总资产智能诊断...";
        try
        {
            var snapshot = await _api.GetSnapshotAsync().ConfigureAwait(true);
            Replace(HealthAssets, snapshot.Health);
            Replace(RootCauses, snapshot.RootCauses);
            Replace(Maintenance, snapshot.Maintenance);
            Replace(Forecasts, snapshot.Forecasts);
            TrackedAssetCount = HealthAssets.Count;
            DegradedAssetCount = HealthAssets.Count(x => x.HealthScore < 70);
            RootCauseCount = RootCauses.Count;
            MaintenanceCount = Maintenance.Count;
            ForecastCount = Forecasts.Count;
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · Assets {TrackedAssetCount} · Degraded {DegradedAssetCount} · RootCause {RootCauseCount} · Maintenance {MaintenanceCount} · Forecast {ForecastCount}";
        }
        catch (Exception ex) { StatusText = $"智能运维读取失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
