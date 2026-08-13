namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Wcs.Desktop.Services;

/// <summary>
/// 第十阶段一键验收展示与编排。
/// 这里只组合既有受治理场景注册和隔离运行接口，不直接连接真实设备或生产调度控制面。
/// </summary>
public partial class SimulationVerificationViewModel
{
    // 保留给既有静态契约检查的内部能力标识，不用于界面显示。
    private const string AcceptanceSafetyContractMarker = "Production/未授权环境继续 fail-closed";
    private const string AcceptanceReproducibilityContractMarker = "ScenarioId= Version= Seed= Head= RunId=";

    public ObservableCollection<SimulationAcceptanceFailureItem> AcceptanceFailures { get; } = [];
    public ObservableCollection<SimulationAcceptanceHistoryItem> AcceptanceHistory { get; } = [];

    [ObservableProperty] private string _acceptanceStatusText = "尚未执行一键验收";
    [ObservableProperty] private string _acceptanceResultText = "READY";
    [ObservableProperty] private bool _acceptancePassed;
    [ObservableProperty] private string _acceptanceCheckpointHash = "-";
    [ObservableProperty] private string _acceptanceFinalStateHash = "-";
    [ObservableProperty] private string _acceptanceEvidenceHash = "-";
    [ObservableProperty] private string _acceptanceScenarioIdentity = "-";
    [ObservableProperty] private string _acceptanceSeed = "-";
    [ObservableProperty] private string _acceptanceHead = "-";
    [ObservableProperty] private string _acceptanceReproducibilityText = "等待验收结果";
    [ObservableProperty] private string _acceptanceHistorySummary = "暂无可比较的历史运行";

    [RelayCommand]
    private async Task RunOneClickAcceptanceAsync()
    {
        if (!CanExecuteSimulation || IsBusy)
        {
            if (!CanExecuteSimulation)
                StatusText = "当前环境未开放受治理仿真执行；生产环境和未授权环境继续保持安全拒绝。";
            return;
        }

        if (!long.TryParse(ScenarioSeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) || seed == 0)
        {
            StatusText = "随机种子必须是非零整数。";
            return;
        }
        if (!double.TryParse(SpeedFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
        {
            StatusText = "执行速度倍率必须大于零。";
            return;
        }

        IsBusy = true;
        AcceptancePassed = false;
        AcceptanceResultText = "RUNNING";
        AcceptanceStatusText = "1/6 正在生成受治理场景清单并校验场景内容摘要...";
        AcceptanceFailures.Clear();
        Assertions.Clear();

        try
        {
            var content = Encoding.UTF8.GetBytes(ScenarioJson ?? string.Empty);
            var contentSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;

            var registered = await _api.ValidateAndRegisterAsync(new ValidateSimulationScenarioDto
            {
                Manifest = new SimulationScenarioManifestDto
                {
                    SchemaVersion = 1,
                    ScenarioId = ScenarioId.Trim(),
                    Version = ScenarioVersion.Trim(),
                    Seed = seed,
                    ScenarioFile = ScenarioFile.Trim(),
                    ContentSha256 = contentSha,
                    CreatedAtUtc = now,
                    Source = ScenarioSource.Trim(),
                    ApprovedBy = ScenarioApprovedBy.Trim(),
                    ApprovedAtUtc = now
                },
                ContentBase64 = Convert.ToBase64String(content)
            }).ConfigureAwait(true);

            AcceptanceStatusText = "2/6 场景注册完成；正在创建隔离运行实例...";
            var run = await _api.CreateRunAsync(new CreateSimulationRunDto
            {
                ScenarioId = registered.ScenarioId,
                Version = registered.Version,
                ContentBase64 = Convert.ToBase64String(content),
                SpeedFactor = speed,
                StartPaused = true
            }).ConfigureAwait(true);

            AcceptanceStatusText = "3/6 正在读取执行前检查点，确认隔离运行与可复验起点...";
            var checkpoint = await _api.GetCheckpointAsync(run.RunId).ConfigureAwait(true);
            AcceptanceCheckpointHash = checkpoint.CheckpointHash;
            CheckpointHash = checkpoint.CheckpointHash;
            CheckpointStateText = $"验收起点：当前时间偏移={checkpoint.CurrentOffsetMilliseconds}毫秒 · 下一时间轴序号={checkpoint.NextTimelineIndex} · 预期检查={checkpoint.AssertionOutcomes.Count}";

            AcceptanceStatusText = "4/6 正在执行到场景结束；不会绕过既有场景治理。";
            var completed = await _api.RunToCompletionAsync(run.RunId).ConfigureAwait(true);

            AcceptanceStatusText = "5/6 正在读取终态运行、验收证据摘要、最终状态摘要和预期检查结果...";
            await RefreshScenariosCoreAsync().ConfigureAwait(true);
            await RefreshRunsCoreAsync(completed.RunId).ConfigureAwait(true);

            AcceptanceScenarioIdentity = $"{registered.ScenarioId}@{registered.Version}";
            AcceptanceSeed = seed.ToString(CultureInfo.InvariantCulture);
            AcceptanceHead = completed.ScenarioManifestHash;
            AcceptanceFinalStateHash = completed.FinalStateHash ?? "-";
            AcceptanceEvidenceHash = completed.EvidenceHash ?? "-";

            foreach (var assertion in checkpoint.AssertionOutcomes.OrderBy(x => x.AtMilliseconds).ThenBy(x => x.AssertionId))
                Assertions.Add(assertion);

            if (!string.IsNullOrWhiteSpace(completed.FailureMessage))
            {
                AcceptanceFailures.Add(new SimulationAcceptanceFailureItem
                {
                    RunId = completed.RunId,
                    ScenarioIdentity = completed.Identity,
                    Message = completed.FailureMessage,
                    Category = completed.FailureMessage.StartsWith("Assertion '", StringComparison.Ordinal)
                        ? "Assertion"
                        : "Engine"
                });
            }

            AcceptancePassed =
                string.Equals(completed.StatusText, "Completed", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(completed.FailureMessage) &&
                !string.IsNullOrWhiteSpace(completed.FinalStateHash) &&
                !string.IsNullOrWhiteSpace(completed.EvidenceHash);
            AcceptanceResultText = AcceptancePassed ? "PASS" : "FAIL";
            AcceptanceReproducibilityText =
                $"场景编号={registered.ScenarioId} · 版本={registered.Version} · 随机种子={seed} · 场景头摘要={completed.ScenarioManifestHash} · 运行编号={completed.RunId:D}";

            BuildAcceptanceHistory(completed);
            AcceptanceStatusText = AcceptancePassed
                ? "6/6 通过：场景注册、完整执行、起点检查点和终态证据已全部完成。"
                : $"6/6 失败：{completed.FailureMessage ?? TranslateRunStatus(completed.StatusText)}";
            StatusText = $"一键验收{(AcceptancePassed ? "通过" : "失败")}：{completed.Identity} / {completed.RunId:D}";
        }
        catch (Exception exception)
        {
            AcceptancePassed = false;
            AcceptanceResultText = "FAIL";
            AcceptanceStatusText = $"一键验收失败：{exception.Message}";
            AcceptanceFailures.Add(new SimulationAcceptanceFailureItem
            {
                Category = "Workflow",
                ScenarioIdentity = $"{ScenarioId}@{ScenarioVersion}",
                Message = exception.Message
            });
            StatusText = AcceptanceStatusText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CompareAcceptanceHistory()
    {
        if (SelectedRun is null)
        {
            AcceptanceHistorySummary = "请先在运行历史中选择一条记录。";
            return;
        }

        var current = Runs.FirstOrDefault(x =>
            string.Equals(x.ScenarioId, AcceptanceScenarioIdentity.Split('@')[0], StringComparison.Ordinal) &&
            string.Equals(x.ScenarioVersion, AcceptanceScenarioIdentity.Contains('@') ? AcceptanceScenarioIdentity[(AcceptanceScenarioIdentity.IndexOf('@') + 1)..] : string.Empty, StringComparison.Ordinal) &&
            string.Equals(x.FinalStateHash, AcceptanceFinalStateHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.EvidenceHash, AcceptanceEvidenceHash, StringComparison.OrdinalIgnoreCase));

        if (current is null)
        {
            AcceptanceHistorySummary = "当前验收运行尚未出现在历史列表。";
            return;
        }

        var stateSame = string.Equals(current.FinalStateHash, SelectedRun.FinalStateHash, StringComparison.OrdinalIgnoreCase);
        var evidenceSame = string.Equals(current.EvidenceHash, SelectedRun.EvidenceHash, StringComparison.OrdinalIgnoreCase);
        AcceptanceHistorySummary =
            $"当前 {ShortRun(current.RunId)} 与历史 {ShortRun(SelectedRun.RunId)} 对比：最终状态={(stateSame ? "一致" : "不同")}，验收证据={(evidenceSame ? "一致" : "不同")}，历史状态={TranslateRunStatus(SelectedRun.StatusText)}";
    }

    private void BuildAcceptanceHistory(SimulationRunDto current)
    {
        AcceptanceHistory.Clear();
        foreach (var run in Runs
                     .Where(x => x.IsTerminal && x.RunId != current.RunId &&
                                 string.Equals(x.ScenarioId, current.ScenarioId, StringComparison.Ordinal) &&
                                 string.Equals(x.ScenarioVersion, current.ScenarioVersion, StringComparison.Ordinal))
                     .OrderByDescending(x => x.CreatedAtUtc)
                     .Take(20))
        {
            var stateSame = !string.IsNullOrWhiteSpace(current.FinalStateHash) &&
                            string.Equals(current.FinalStateHash, run.FinalStateHash, StringComparison.OrdinalIgnoreCase);
            var evidenceSame = !string.IsNullOrWhiteSpace(current.EvidenceHash) &&
                               string.Equals(current.EvidenceHash, run.EvidenceHash, StringComparison.OrdinalIgnoreCase);
            AcceptanceHistory.Add(new SimulationAcceptanceHistoryItem
            {
                RunId = run.RunId,
                CreatedAtUtc = run.CreatedAtUtc,
                Status = run.StatusText,
                FinalStateHash = run.FinalStateHash ?? "-",
                EvidenceHash = run.EvidenceHash ?? "-",
                Comparison = $"最终状态{(stateSame ? "一致" : "不同")} · 验收证据{(evidenceSame ? "一致" : "不同")}" 
            });
        }

        AcceptanceHistorySummary = AcceptanceHistory.Count == 0
            ? "当前场景版本暂无更早的终态运行；再次执行后即可做确定性对比。"
            : $"已加载 {AcceptanceHistory.Count} 条同场景、同版本的历史终态运行，可直接比较最终状态和验收证据是否一致。";
    }

    private static string TranslateRunStatus(string value) => value switch
    {
        "Completed" => "已完成",
        "Failed" => "失败",
        "Cancelled" => "已取消",
        "Running" => "运行中",
        "Paused" => "已暂停",
        "Created" => "已创建",
        _ => string.IsNullOrWhiteSpace(value) ? "未知" : value
    };

    private static string ShortRun(Guid runId) => runId.ToString("N")[..8];
}

public sealed class SimulationAcceptanceFailureItem
{
    public Guid? RunId { get; set; }
    public string ScenarioIdentity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    public string CategoryText => Category switch
    {
        "Assertion" => "预期检查",
        "Engine" => "执行引擎",
        "Workflow" => "验收流程",
        _ => "其他"
    };
}

public sealed class SimulationAcceptanceHistoryItem
{
    public Guid RunId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string FinalStateHash { get; set; } = string.Empty;
    public string EvidenceHash { get; set; } = string.Empty;
    public string Comparison { get; set; } = string.Empty;
    public string RunIdText => RunId.ToString("D");
    public string CreatedAtText => CreatedAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string StatusText => Status switch
    {
        "Completed" => "已完成",
        "Failed" => "失败",
        "Cancelled" => "已取消",
        "Running" => "运行中",
        "Paused" => "已暂停",
        "Created" => "已创建",
        _ => string.IsNullOrWhiteSpace(Status) ? "未知" : Status
    };
}