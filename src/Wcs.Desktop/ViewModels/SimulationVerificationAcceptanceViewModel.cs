namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Wcs.Desktop.Services;

/// <summary>
/// Batch D acceptance closure for the existing governed S0-S10 simulator.
/// This partial ViewModel never talks to PLC/RGV/production dispatch APIs directly;
/// it only composes the already-authorized S0 governance and S1 Run APIs exposed by
/// ISimulationVerificationApiService.
/// </summary>
public partial class SimulationVerificationViewModel
{
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
    [ObservableProperty] private string _acceptanceHistorySummary = "暂无可比较历史 Run";

    [RelayCommand]
    private async Task RunOneClickAcceptanceAsync()
    {
        if (!CanExecuteSimulation || IsBusy)
        {
            if (!CanExecuteSimulation)
                StatusText = "当前环境未开放受治理 Simulation 执行；Production/未授权环境继续 fail-closed。";
            return;
        }

        if (!long.TryParse(ScenarioSeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) || seed == 0)
        {
            StatusText = "Seed 必须是非 0 Int64。";
            return;
        }
        if (!double.TryParse(SpeedFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
        {
            StatusText = "Speed Factor 必须大于 0。";
            return;
        }

        IsBusy = true;
        AcceptancePassed = false;
        AcceptanceResultText = "RUNNING";
        AcceptanceStatusText = "1/6 正在生成治理 Manifest 并校验 Scenario DSL / Content SHA-256...";
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

            AcceptanceStatusText = "2/6 S0 注册完成；正在通过 S1 创建隔离 Run（StartPaused=true）...";
            var run = await _api.CreateRunAsync(new CreateSimulationRunDto
            {
                ScenarioId = registered.ScenarioId,
                Version = registered.Version,
                ContentBase64 = Convert.ToBase64String(content),
                SpeedFactor = speed,
                StartPaused = true
            }).ConfigureAwait(true);

            AcceptanceStatusText = "3/6 正在读取执行前 Checkpoint，确认 RunId 隔离与可复验起点...";
            var checkpoint = await _api.GetCheckpointAsync(run.RunId).ConfigureAwait(true);
            AcceptanceCheckpointHash = checkpoint.CheckpointHash;
            CheckpointHash = checkpoint.CheckpointHash;
            CheckpointStateText = $"验收起点：Offset={checkpoint.CurrentOffsetMilliseconds}ms · Next={checkpoint.NextTimelineIndex} · Assertions={checkpoint.AssertionOutcomes.Count}";

            AcceptanceStatusText = "4/6 正在调用现有 S1 Run To End；不绕过 Scenario governance...";
            var completed = await _api.RunToCompletionAsync(run.RunId).ConfigureAwait(true);

            AcceptanceStatusText = "5/6 正在读取终态 Run、Evidence / FinalState Hash 与断言摘要...";
            await RefreshScenariosCoreAsync().ConfigureAwait(true);
            await RefreshRunsCoreAsync(completed.RunId).ConfigureAwait(true);

            AcceptanceScenarioIdentity = $"{registered.ScenarioId}@{registered.Version}";
            AcceptanceSeed = seed.ToString(CultureInfo.InvariantCulture);
            // The governed manifest hash is the immutable execution head accepted by S0 for this version.
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
                $"ScenarioId={registered.ScenarioId} · Version={registered.Version} · Seed={seed} · Head={completed.ScenarioManifestHash} · RunId={completed.RunId:D}";

            BuildAcceptanceHistory(completed);
            AcceptanceStatusText = AcceptancePassed
                ? "6/6 PASS：治理注册、Run To End、Checkpoint 与终态 Evidence/State Hash 已收口。"
                : $"6/6 FAIL：{completed.FailureMessage ?? completed.StatusText}";
            StatusText = $"一键验收 {AcceptanceResultText}：{completed.Identity} / {completed.RunId:D}";
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
            AcceptanceHistorySummary = "请先在 Run 历史中选择一条记录。";
            return;
        }

        var current = Runs.FirstOrDefault(x =>
            string.Equals(x.ScenarioId, AcceptanceScenarioIdentity.Split('@')[0], StringComparison.Ordinal) &&
            string.Equals(x.ScenarioVersion, AcceptanceScenarioIdentity.Contains('@') ? AcceptanceScenarioIdentity[(AcceptanceScenarioIdentity.IndexOf('@') + 1)..] : string.Empty, StringComparison.Ordinal) &&
            string.Equals(x.FinalStateHash, AcceptanceFinalStateHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.EvidenceHash, AcceptanceEvidenceHash, StringComparison.OrdinalIgnoreCase));

        if (current is null)
        {
            AcceptanceHistorySummary = "当前验收 Run 尚未出现在历史列表。";
            return;
        }

        var stateSame = string.Equals(current.FinalStateHash, SelectedRun.FinalStateHash, StringComparison.OrdinalIgnoreCase);
        var evidenceSame = string.Equals(current.EvidenceHash, SelectedRun.EvidenceHash, StringComparison.OrdinalIgnoreCase);
        AcceptanceHistorySummary =
            $"当前 {ShortRun(current.RunId)} vs 历史 {ShortRun(SelectedRun.RunId)}：FinalStateHash={(stateSame ? "一致" : "不同")}，EvidenceHash={(evidenceSame ? "一致" : "不同")}，Status={SelectedRun.StatusText}";
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
                Comparison = $"State {(stateSame ? "=" : "≠")} · Evidence {(evidenceSame ? "=" : "≠")}" 
            });
        }

        AcceptanceHistorySummary = AcceptanceHistory.Count == 0
            ? "当前 Scenario 版本暂无更早终态 Run；再次执行后即可做确定性对比。"
            : $"已加载 {AcceptanceHistory.Count} 条同 Scenario/Version 历史终态 Run；= 表示 Hash 可复验一致。";
    }

    private static string ShortRun(Guid runId) => runId.ToString("N")[..8];
}

public sealed class SimulationAcceptanceFailureItem
{
    public Guid? RunId { get; set; }
    public string ScenarioIdentity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
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
}