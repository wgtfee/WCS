namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Wcs.Desktop.Services;

public partial class SimulationVerificationViewModel : ViewModelBase
{
    private readonly ISimulationVerificationApiService _api;

    public ObservableCollection<SimulationVerificationStageDto> Stages { get; } = [];
    public ObservableCollection<RegisteredSimulationScenarioDto> Scenarios { get; } = [];
    public ObservableCollection<SimulationRunDto> Runs { get; } = [];
    public ObservableCollection<SimulationAssertionOutcomeDto> Assertions { get; } = [];

    [ObservableProperty] private SimulationVerificationStageDto? _selectedStage;
    [ObservableProperty] private RegisteredSimulationScenarioDto? _selectedScenario;
    [ObservableProperty] private SimulationRunDto? _selectedRun;
    [ObservableProperty] private string _environment = "Unknown";
    [ObservableProperty] private string _modeText = "未连接";
    [ObservableProperty] private string _simulationText = "未知";
    [ObservableProperty] private string _hilText = "未知";
    [ObservableProperty] private string _realHilText = "Pending";
    [ObservableProperty] private string _safetyText = "S10 总览只读；仿真执行仅允许走受治理 Scenario API";
    [ObservableProperty] private int _availableStageCount;
    [ObservableProperty] private int _totalStageCount;
    [ObservableProperty] private int _registeredScenarioCount;
    [ObservableProperty] private int _runCount;
    [ObservableProperty] private int _failedRunCount;
    [ObservableProperty] private bool _canExecuteSimulation;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private string _scenarioId = "desktop-smoke";
    [ObservableProperty] private string _scenarioVersion = "1.0.0";
    [ObservableProperty] private string _scenarioSeedText = "20260810";
    [ObservableProperty] private string _scenarioFile = "desktop-smoke.json";
    [ObservableProperty] private string _scenarioSource = "Wcs.Desktop Simulation Verification";
    [ObservableProperty] private string _scenarioApprovedBy = "simulation-operator";
    [ObservableProperty] private string _scenarioJson = BuildSampleScenario();
    [ObservableProperty] private string _speedFactorText = "1";
    [ObservableProperty] private string _checkpointHash = "-";
    [ObservableProperty] private string _checkpointStateText = "尚未创建 Checkpoint";
    [ObservableProperty] private string _executionBoundaryText = "仅 Simulation / SimulationLoadTest 可执行";

    public SimulationVerificationViewModel(ISimulationVerificationApiService api)
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
        StatusText = "正在读取 S0～S10 验证状态与受治理仿真运行...";
        try
        {
            var overview = await _api.GetOverviewAsync().ConfigureAwait(true);
            if (overview is null)
            {
                ClearUnavailable();
                StatusText = "当前 Host 环境未开放统一验证中心。Production 或未授权环境会按设计返回 404。";
                return;
            }

            ApplyOverview(overview);
            if (CanExecuteSimulation)
            {
                await RefreshScenariosCoreAsync().ConfigureAwait(true);
                await RefreshRunsCoreAsync().ConfigureAwait(true);
            }
            else
            {
                Scenarios.Clear();
                Runs.Clear();
                Assertions.Clear();
                RegisteredScenarioCount = 0;
                RunCount = 0;
                FailedRunCount = 0;
            }

            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception exception)
        {
            StatusText = $"读取失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void LoadSampleScenario()
    {
        ScenarioId = "desktop-smoke";
        ScenarioVersion = "1.0.0";
        ScenarioSeedText = "20260810";
        ScenarioFile = "desktop-smoke.json";
        ScenarioSource = "Wcs.Desktop Simulation Verification";
        ScenarioApprovedBy = "simulation-operator";
        ScenarioJson = BuildSampleScenario();
        SpeedFactorText = "1";
        StatusText = "已载入 S1 确定性 Smoke 场景。可在隔离仿真环境中校验/注册后运行。";
    }

    [RelayCommand]
    private async Task RegisterScenarioAsync()
    {
        if (!CanExecuteSimulation || IsBusy)
            return;

        if (!long.TryParse(ScenarioSeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed) || seed == 0)
        {
            StatusText = "Seed 必须是非 0 Int64。";
            return;
        }

        IsBusy = true;
        StatusText = "正在校验 Scenario DSL、SHA-256 和治理 Manifest...";
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

            await RefreshScenariosCoreAsync().ConfigureAwait(true);
            SelectedScenario = Scenarios.FirstOrDefault(x =>
                x.ScenarioId == registered.ScenarioId && x.Version == registered.Version);
            StatusText = $"治理校验通过并已注册 {registered.Identity}，ManifestHash={ShortHash(registered.ManifestHash)}";
        }
        catch (Exception exception)
        {
            StatusText = $"场景注册失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateRunAsync()
    {
        if (!CanExecuteSimulation || IsBusy)
            return;

        if (!double.TryParse(SpeedFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
        {
            StatusText = "Speed Factor 必须大于 0。";
            return;
        }

        IsBusy = true;
        StatusText = "正在创建受治理仿真 Run（默认 Paused）...";
        try
        {
            var run = await _api.CreateRunAsync(new CreateSimulationRunDto
            {
                ScenarioId = ScenarioId.Trim(),
                Version = ScenarioVersion.Trim(),
                ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ScenarioJson ?? string.Empty)),
                SpeedFactor = speed,
                StartPaused = true
            }).ConfigureAwait(true);

            await RefreshRunsCoreAsync(run.RunId).ConfigureAwait(true);
            Assertions.Clear();
            CheckpointHash = "-";
            CheckpointStateText = "Run 已创建；可 Step、推进虚拟时间或运行到完成。";
            StatusText = $"Run {run.RunId:D} 已创建，状态 {run.StatusText}。";
        }
        catch (Exception exception)
        {
            StatusText = $"创建 Run 失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task StepAsync() => ExecuteSelectedRunAsync((id) => _api.StepAsync(id), "单步执行");

    [RelayCommand]
    private Task RunToCompletionAsync() => ExecuteSelectedRunAsync((id) => _api.RunToCompletionAsync(id), "运行到完成");

    [RelayCommand]
    private Task PauseAsync() => ExecuteSelectedRunAsync((id) => _api.PauseAsync(id), "暂停");

    [RelayCommand]
    private Task ResumeAsync() => ExecuteSelectedRunAsync((id) => _api.ResumeAsync(id), "恢复");

    [RelayCommand]
    private Task CancelRunAsync() => ExecuteSelectedRunAsync((id) => _api.CancelAsync(id), "取消");

    [RelayCommand]
    private Task Advance10SecondsAsync()
    {
        if (SelectedRun is null)
            return SetNoRunSelectedAsync();
        var target = checked(SelectedRun.CurrentOffsetMilliseconds + 10_000L);
        return ExecuteSelectedRunAsync((id) => _api.AdvanceAsync(id, target), "虚拟时间 +10s");
    }

    [RelayCommand]
    private async Task SetSpeedAsync()
    {
        if (!double.TryParse(SpeedFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
        {
            StatusText = "Speed Factor 必须大于 0。";
            return;
        }

        await ExecuteSelectedRunAsync((id) => _api.SetSpeedAsync(id, speed), $"设置速度 {speed:0.###}x");
    }

    [RelayCommand]
    private async Task ReadCheckpointAsync()
    {
        if (!CanExecuteSimulation || IsBusy || SelectedRun is null)
        {
            if (SelectedRun is null)
                StatusText = "请先选择一个非终态 Run。";
            return;
        }

        IsBusy = true;
        try
        {
            var checkpoint = await _api.GetCheckpointAsync(SelectedRun.RunId).ConfigureAwait(true);
            CheckpointHash = checkpoint.CheckpointHash;
            CheckpointStateText = $"Offset={checkpoint.CurrentOffsetMilliseconds}ms · Next={checkpoint.NextTimelineIndex} · Assertions={checkpoint.AssertionOutcomes.Count}";
            Assertions.Clear();
            foreach (var assertion in checkpoint.AssertionOutcomes.OrderBy(x => x.AtMilliseconds).ThenBy(x => x.AssertionId))
                Assertions.Add(assertion);
            StatusText = $"Checkpoint 已校验：{ShortHash(checkpoint.CheckpointHash)}";
        }
        catch (Exception exception)
        {
            StatusText = $"读取 Checkpoint 失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteSelectedRunAsync(
        Func<Guid, Task<SimulationRunDto>> command,
        string operation)
    {
        if (!CanExecuteSimulation || IsBusy || SelectedRun is null)
        {
            if (SelectedRun is null)
                StatusText = "请先选择一个 Run。";
            return;
        }

        IsBusy = true;
        var runId = SelectedRun.RunId;
        StatusText = $"正在{operation}...";
        try
        {
            var updated = await command(runId).ConfigureAwait(true);
            await RefreshRunsCoreAsync(updated.RunId).ConfigureAwait(true);
            StatusText = $"{operation}完成：{updated.StatusText}，Offset={updated.CurrentOffsetMilliseconds}ms，Timeline={updated.ProgressText}";
        }
        catch (Exception exception)
        {
            StatusText = $"{operation}失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task SetNoRunSelectedAsync()
    {
        StatusText = "请先选择一个 Run。";
        return Task.CompletedTask;
    }

    private void ApplyOverview(SimulationVerificationOverviewDto overview)
    {
        Environment = overview.Environment;
        CanExecuteSimulation = overview.SimulationInspectionAvailable &&
                               !string.Equals(overview.Environment, "Production", StringComparison.OrdinalIgnoreCase);
        ModeText = CanExecuteSimulation
            ? "S10 总览 + S1 受治理执行"
            : overview.ReadOnly && !overview.RemoteControlAllowed ? "只读检查" : "安全拒绝";
        SimulationText = CanExecuteSimulation ? "可执行受治理场景" : "当前环境不可用";
        HilText = overview.HilInspectionAvailable ? "只读检查可用" : "当前环境不可用";
        RealHilText = overview.RealHilExecuted && overview.ProtocolValidated &&
                      overview.MechanicalSafetyAccepted && overview.SiteAccepted
            ? "Accepted"
            : "Pending";
        SafetyText = CanExecuteSimulation
            ? "S10 元数据保持只读；执行仅复用 S0/S1 受治理 Scenario API。S2～S8 故障、RGV、Traffic、External、Recovery、Capacity 必须通过 DSL 与 RunId 隔离执行。"
            : "当前环境只允许查看已授权证据；不开放任何仿真执行入口。";
        ExecutionBoundaryText = CanExecuteSimulation
            ? "仅操作进程内 Simulation State；Production、真实 PLC/RGV/HIL 始终不在该控制面内"
            : "仿真执行已被环境边界禁用";

        Stages.Clear();
        foreach (var stage in overview.Stages.OrderBy(x => ParseStageNumber(x.Id)))
            Stages.Add(stage);

        TotalStageCount = Stages.Count;
        AvailableStageCount = Stages.Count(x => x.Availability == "Available");
        SelectedStage = Stages.FirstOrDefault();
    }

    private async Task RefreshScenariosCoreAsync()
    {
        var selectedIdentity = SelectedScenario?.Identity;
        var scenarios = await _api.GetScenariosAsync().ConfigureAwait(true);
        Scenarios.Clear();
        foreach (var scenario in scenarios.OrderBy(x => x.ScenarioId).ThenBy(x => x.Version))
            Scenarios.Add(scenario);
        RegisteredScenarioCount = Scenarios.Count;
        SelectedScenario = Scenarios.FirstOrDefault(x => x.Identity == selectedIdentity) ?? Scenarios.FirstOrDefault();
    }

    private async Task RefreshRunsCoreAsync(Guid? selectRunId = null)
    {
        var selectedId = selectRunId ?? SelectedRun?.RunId;
        var runs = await _api.GetRunsAsync().ConfigureAwait(true);
        Runs.Clear();
        foreach (var run in runs.OrderByDescending(x => x.CreatedAtUtc))
            Runs.Add(run);
        RunCount = Runs.Count;
        FailedRunCount = Runs.Count(x => x.StatusText is "Failed" or "Cancelled");
        SelectedRun = selectedId.HasValue
            ? Runs.FirstOrDefault(x => x.RunId == selectedId.Value) ?? Runs.FirstOrDefault()
            : Runs.FirstOrDefault();
    }

    private void ClearUnavailable()
    {
        Environment = "Unavailable";
        ModeText = "安全拒绝";
        SimulationText = "不可用";
        HilText = "不可用";
        RealHilText = "Pending";
        SafetyText = "没有开放任何仿真或 HIL 控制入口。";
        ExecutionBoundaryText = "Production/未授权环境 fail-closed";
        CanExecuteSimulation = false;
        AvailableStageCount = 0;
        TotalStageCount = 0;
        RegisteredScenarioCount = 0;
        RunCount = 0;
        FailedRunCount = 0;
        SelectedStage = null;
        SelectedScenario = null;
        SelectedRun = null;
        Stages.Clear();
        Scenarios.Clear();
        Runs.Clear();
        Assertions.Clear();
    }

    private static string ShortHash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value[..Math.Min(12, value.Length)];

    private static int ParseStageNumber(string value) =>
        value.Length > 1 && int.TryParse(value[1..], out var number) ? number : int.MaxValue;

    private static string BuildSampleScenario() =>
        "{\"SchemaVersion\":1,\"ScenarioId\":\"desktop-smoke\",\"Version\":\"1.0.0\",\"Seed\":20260810,\"StartTimeUtc\":\"2026-08-10T00:00:00+00:00\",\"DurationMilliseconds\":60000,\"StopOnAssertionFailure\":true,\"Actions\":[{\"Id\":\"set-ready\",\"AtMilliseconds\":1000,\"Order\":0,\"Kind\":\"state.set\",\"Target\":\"desktop.ready\",\"Payload\":true}],\"Assertions\":[{\"Id\":\"assert-ready\",\"AtMilliseconds\":2000,\"Order\":0,\"Kind\":\"state.equals\",\"Target\":\"desktop.ready\",\"Expected\":true}]}";
}
