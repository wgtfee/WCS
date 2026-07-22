using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Core.TransportScheduling;
using Wcs.Desktop.Services;

namespace Wcs.Desktop.ViewModels;

public partial class TransportResilienceViewModel : ViewModelBase
{
    private readonly ITransportResilienceApiService _api;

    public ObservableCollection<TransportReadinessCheckItem> ReadinessChecks { get; } = new();
    public ObservableCollection<TransportOperationalBaseline> Baselines { get; } = new();
    public ObservableCollection<TransportLogicalBackupManifest> Backups { get; } = new();
    public ObservableCollection<TransportRecoveryDrillReport> Drills { get; } = new();
    public ObservableCollection<TransportBackupValidationIssue> ValidationIssues { get; } = new();

    [ObservableProperty] private string _readinessState = "NotEvaluated";
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _backupCount;
    [ObservableProperty] private int _drillCount;
    [ObservableProperty] private string _lastBackupText = "无";
    [ObservableProperty] private string _lastDrillText = "无";
    [ObservableProperty] private TransportLogicalBackupManifest? _selectedBackup;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";

    public TransportResilienceViewModel(ITransportResilienceApiService api)
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
        StatusText = "正在读取生产韧性状态...";
        try
        {
            var summaryTask = _api.GetSummaryAsync();
            var baselineTask = _api.GetBaselinesAsync(100);
            var backupTask = _api.GetBackupsAsync(100);
            var drillTask = _api.GetDrillsAsync(100);
            await Task.WhenAll(summaryTask, baselineTask, backupTask, drillTask);

            ApplySummary(summaryTask.Result ?? new TransportResilienceSnapshot());
            Replace(Baselines, baselineTask.Result);
            Replace(Backups, backupTask.Result);
            Replace(Drills, drillTask.Result);
            if (SelectedBackup is not null)
                SelectedBackup = Backups.FirstOrDefault(x => x.BackupId == SelectedBackup.BackupId);
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

    [RelayCommand]
    private async Task RunReadinessAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusText = "正在执行生产就绪检查...";
        try
        {
            var report = await _api.RunReadinessAsync();
            if (report is not null)
            {
                ApplyReadiness(report);
                StatusText = report.IsReady
                    ? "生产就绪检查通过"
                    : $"生产就绪检查未通过：Critical={report.CriticalCount}, Error={report.ErrorCount}, Warning={report.WarningCount}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"生产就绪检查失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ValidateSelectedBackupAsync()
    {
        if (IsBusy || SelectedBackup is null)
            return;
        IsBusy = true;
        StatusText = $"正在校验备份 {SelectedBackup.BackupId}...";
        try
        {
            var report = await _api.ValidateBackupAsync(SelectedBackup.BackupId);
            Replace(ValidationIssues, report?.Issues ?? Array.Empty<TransportBackupValidationIssue>());
            StatusText = report?.CanPrepareConfigurationRestore == true
                ? "备份完整，可准备配置恢复快照"
                : "备份校验未通过或包含阻断项";
        }
        catch (Exception ex)
        {
            StatusText = $"备份校验失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySummary(TransportResilienceSnapshot summary)
    {
        if (summary.LastReadiness is not null)
            ApplyReadiness(summary.LastReadiness);
        else
            ReadinessState = "NotEvaluated";
        BackupCount = summary.BackupCount;
        DrillCount = summary.DrillCount;
        LastBackupText = summary.LastBackup is null
            ? "无"
            : $"{summary.LastBackup.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} / {summary.LastBackup.BackupId[..8]}";
        LastDrillText = summary.LastDrill is null
            ? "无"
            : $"{summary.LastDrill.Scenario} / {(summary.LastDrill.Passed ? "通过" : "未通过")}";
    }

    private void ApplyReadiness(TransportReadinessReport report)
    {
        ReadinessState = report.IsReady ? "Ready" : "NotReady";
        CriticalCount = report.CriticalCount;
        ErrorCount = report.ErrorCount;
        WarningCount = report.WarningCount;
        Replace(ReadinessChecks, report.Checks);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
