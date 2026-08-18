namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;

public partial class IndustrialIntelligenceOverviewViewModel : ViewModelBase
{
    private readonly IIndustrialIntelligenceOverviewApiService _api;

    public ObservableCollection<IntelligenceStageStatusDto> Stages { get; } = [];

    [ObservableProperty] private int _availableStageCount;
    [ObservableProperty] private int _failClosedStageCount;
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private bool _isBusy;

    public string BoundaryText => "IDI P0-P6 当前仍以治理、模型、特征、建议、学习、仿真优化和软件侧自动化就绪为主。此总览不提升任何自动化等级；Production/ControlWrite 状态直接来自各阶段 API。";

    public IndustrialIntelligenceOverviewViewModel(IIndustrialIntelligenceOverviewApiService api) => _api = api;

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在汇总 IDI P0-P6...";
        try
        {
            var values = await _api.GetStagesAsync().ConfigureAwait(true);
            Stages.Clear();
            foreach (var item in values.OrderBy(x => x.Stage)) Stages.Add(item);
            AvailableStageCount = Stages.Count(x => x.Available);
            FailClosedStageCount = Stages.Count - AvailableStageCount;
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · Available {AvailableStageCount}/7 · FailClosed/Unavailable {FailClosedStageCount}";
        }
        catch (Exception ex) { StatusText = $"总览读取失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
}
