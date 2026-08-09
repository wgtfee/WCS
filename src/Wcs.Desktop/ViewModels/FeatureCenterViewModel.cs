namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;

public partial class FeatureCenterViewModel : ViewModelBase
{
    private readonly IFeatureCenterApiService _api;

    public ObservableCollection<FeatureDefinitionDto> Features { get; } = [];

    [ObservableProperty] private int _featureCount;
    [ObservableProperty] private int _entityTypeCount;
    [ObservableProperty] private int _sourceCount;
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _schemaId = string.Empty;
    [ObservableProperty] private string _schemaVersion = string.Empty;
    [ObservableProperty] private string _snapshotId = string.Empty;
    [ObservableProperty] private string _datasetId = string.Empty;
    [ObservableProperty] private string _datasetVersion = string.Empty;
    [ObservableProperty] private string _schemaText = "未加载 Schema";
    [ObservableProperty] private string _snapshotText = "未加载 Snapshot";
    [ObservableProperty] private string _datasetText = "未加载 Dataset";

    public string SafetyText => "Feature Center 是只读特征治理/追溯入口。Definition、Schema、Snapshot、Dataset 用于模型和决策复现；本页面不写 PLC、不改任务、车辆、路线或交通状态。";

    public FeatureCenterViewModel(IFeatureCenterApiService api) => _api = api;

    protected override Task OnInitializeAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取 Feature Definitions...";
        try
        {
            var values = await _api.GetFeaturesAsync().ConfigureAwait(true);
            Features.Clear();
            foreach (var item in values.OrderBy(x => x.EntityType).ThenBy(x => x.FeatureId)) Features.Add(item);
            FeatureCount = Features.Count;
            EntityTypeCount = Features.Select(x => x.EntityType).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            SourceCount = Features.Select(x => x.Source).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · Feature {FeatureCount} · EntityType {EntityTypeCount} · Source {SourceCount}";
        }
        catch (Exception ex)
        {
            StatusText = $"读取失败（Feature Center 保持隔离）：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadSchemaAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SchemaId) || string.IsNullOrWhiteSpace(SchemaVersion)) return;
        IsBusy = true;
        try
        {
            var value = await _api.GetSchemaAsync(SchemaId.Trim(), SchemaVersion.Trim()).ConfigureAwait(true);
            SchemaText = value is null
                ? "未找到 Schema"
                : $"{value.SchemaId}/{value.Version} · Status={value.Status} · Items={value.Items.Count} · ApprovedBy={value.ApprovedBy} · Hash={value.SchemaHash}";
        }
        catch (Exception ex) { SchemaText = $"Schema 查询失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadSnapshotAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(SnapshotId)) return;
        IsBusy = true;
        try
        {
            var value = await _api.GetSnapshotAsync(SnapshotId.Trim()).ConfigureAwait(true);
            SnapshotText = value is null
                ? "未找到 Snapshot"
                : $"{value.SnapshotId} · Entity={value.EntityId} · AsOf={value.AsOfUtc:O} · Schema={value.FeatureSchemaId} · Quality={value.QualityStatus} · Values={value.Values.Count} · Hash={value.ValuesHash}";
        }
        catch (Exception ex) { SnapshotText = $"Snapshot 查询失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadDatasetAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(DatasetId) || string.IsNullOrWhiteSpace(DatasetVersion)) return;
        IsBusy = true;
        try
        {
            var value = await _api.GetDatasetAsync(DatasetId.Trim(), DatasetVersion.Trim()).ConfigureAwait(true);
            DatasetText = value is null
                ? "未找到 Dataset"
                : $"{value.DatasetId}/{value.Version} · Rows={value.RowCount:N0} · Range={value.FromUtc:O} ~ {value.ToUtc:O} · Schema={value.FeatureSchemaId} · DatasetHash={value.DatasetHash} · Storage={value.StorageUri}";
        }
        catch (Exception ex) { DatasetText = $"Dataset 查询失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }
}
