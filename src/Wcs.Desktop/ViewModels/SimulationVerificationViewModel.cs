namespace Wcs.Desktop.ViewModels;

using System.Text.Json;
using System.Text.Json.Nodes;
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
    private bool _suppressScenarioAutoSync;
    private CancellationTokenSource? _runPollCts;

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

    protected override Task OnInitializeAsync()
    {
        StartRunPolling();
        return RefreshAsync();
    }

    protected override void OnDispose()
    {
        _runPollCts?.Cancel();
        _runPollCts?.Dispose();
        _runPollCts = null;
        base.OnDispose();
    }

    // ==================== 场景元数据自动同步 ====================
    //
    // ScenarioJson 是唯一事实来源：编辑内容后自动回填表单字段，
    // 消除"表单与 JSON 双份输入漂移 → 运行时报 SHA-256 不匹配"的坑。

    partial void OnScenarioJsonChanged(string value)
    {
        if (_suppressScenarioAutoSync)
        {
            // 显式载入示例等场景：跳过自动回填，但仍刷新数据预览
            RebuildScenarioDataPreview(value);
            return;
        }

        RebuildScenarioDataPreview(value);

        var error = SyncScenarioMetadataFromJson(value);
        if (error != null && !IsDisposed)
            StatusText = error;
    }

    /// <summary>以场景 JSON 为准回填 ScenarioId/Version/Seed/File。返回 null 表示就绪，否则为友好错误。</summary>
    private string? SyncScenarioMetadataFromJson(string? json)
    {
        JsonObject? node;
        try
        {
            node = JsonNode.Parse(json ?? string.Empty) as JsonObject;
        }
        catch (JsonException exception)
        {
            return $"场景 JSON 无法解析（第 {exception.LineNumber + 1} 行）：{exception.GetBaseException().Message}";
        }

        if (node is null)
            return "场景内容必须是 JSON 对象。";

        var id = node["ScenarioId"] is { } idNode && idNode.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? idNode.GetValue<string>()?.Trim()
            : null;
        var version = node["Version"] is { } versionNode && versionNode.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? versionNode.GetValue<string>()?.Trim()
            : null;
        long? seed = node["Seed"] is JsonValue seedValue && seedValue.TryGetValue<long>(out var parsedSeed)
            ? parsedSeed
            : null;

        if (string.IsNullOrWhiteSpace(id))
            return "场景 JSON 缺少 ScenarioId。";
        if (string.IsNullOrWhiteSpace(version))
            return "场景 JSON 缺少 Version。";
        if (seed is null or 0)
            return "场景 JSON 的 Seed 必须是非 0 整数。";

        ScenarioId = id;
        ScenarioVersion = version;
        ScenarioSeedText = seed.Value.ToString(CultureInfo.InvariantCulture);
        ScenarioFile = $"{id}-{version}.json";
        return null;
    }

    /// <summary>提交前调用：确保元数据与场景内容一致，不一致时直接给出可读错误而不是等服务端 SHA-256 校验失败。</summary>
    private bool TryEnsureScenarioMetadataReady()
    {
        var error = SyncScenarioMetadataFromJson(ScenarioJson);
        if (error != null)
        {
            StatusText = error;
            return false;
        }
        return true;
    }

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
                StatusText = "当前 Host 环境未开放统一验证中心（Production 或未授权环境按设计返回 404）。"
                    + "启用方法见下方安全边界说明；本地调试请使用 Simulation 环境。";
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
        _suppressScenarioAutoSync = true;
        try
        {
            ScenarioId = "desktop-smoke";
            ScenarioVersion = "1.0.0";
            ScenarioSeedText = "20260810";
            ScenarioFile = "desktop-smoke.json";
            ScenarioSource = "Wcs.Desktop Simulation Verification";
            ScenarioApprovedBy = "simulation-operator";
            ScenarioJson = BuildSampleScenario();
            SpeedFactorText = "1";
        }
        finally
        {
            _suppressScenarioAutoSync = false;
        }
        StatusText = "已载入 S1 确定性 Smoke 场景。可直接「校验并注册」后创建 Run；编辑场景内容会自动回填元数据字段。";
    }

    [RelayCommand]
    private async Task RegisterScenarioAsync()
    {
        if (!CanExecuteSimulation || IsBusy)
            return;

        // 以 JSON 为唯一事实来源回填并校验元数据，避免双份输入漂移
        if (!TryEnsureScenarioMetadataReady())
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

        if (!TryEnsureScenarioMetadataReady())
            return;

        if (!double.TryParse(SpeedFactorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
        {
            StatusText = "Speed Factor 必须大于 0。";
            return;
        }

        IsBusy = true;
        StatusText = "正在创建受治理仿真 Run（默认暂停，创建后点「运行到完成」开始执行）...";
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
            CheckpointStateText = "Run 已创建（暂停）。点「运行到完成」直接跑完，或用「单步」「+10 秒」逐段观察。";
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
    private Task RunToCompletionAsync() =>
        ExecuteSelectedRunAsync((id) => _api.RunToCompletionAsync(id), "运行到完成", loadCheckpointAfter: true);

    [RelayCommand]
    private Task PauseAsync() => ExecuteSelectedRunAsync((id) => _api.PauseAsync(id), "暂停");

    /// <summary>
    /// 恢复并继续执行到完成。
    /// 引擎是按需推进的（恢复暂停本身不会让时间前进），
    /// 因此界面上的"恢复"直接衔接「运行到完成」，避免点了没反应的困惑；
    /// 需要手动逐段观察时使用「单步」「+10 秒」。
    /// </summary>
    [RelayCommand]
    private Task ResumeAsync() => ExecuteSelectedRunAsync(
        async id =>
        {
            var resumed = await _api.ResumeAsync(id);
            return resumed.IsTerminal ? resumed : await _api.RunToCompletionAsync(id);
        },
        "从暂停处继续执行",
        loadCheckpointAfter: true);

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
            await ReadCheckpointCoreAsync(SelectedRun.RunId).ConfigureAwait(true);
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

    /// <summary>无守卫版本的检查点读取，供命令与自动轮询复用。调用方负责 IsBusy 与异常处理。</summary>
    private async Task ReadCheckpointCoreAsync(Guid runId)
    {
        var checkpoint = await _api.GetCheckpointAsync(runId).ConfigureAwait(true);
        CheckpointHash = checkpoint.CheckpointHash;
        CheckpointStateText = $"Offset={checkpoint.CurrentOffsetMilliseconds}ms · Next={checkpoint.NextTimelineIndex} · 断言={checkpoint.AssertionOutcomes.Count} 条";
        Assertions.Clear();
        foreach (var assertion in checkpoint.AssertionOutcomes.OrderBy(x => x.AtMilliseconds).ThenBy(x => x.AssertionId))
            Assertions.Add(assertion);
        StatusText = $"Checkpoint 已校验：{ShortHash(checkpoint.CheckpointHash)}";
    }

    private async Task ExecuteSelectedRunAsync(
        Func<Guid, Task<SimulationRunDto>> command,
        string operation,
        bool loadCheckpointAfter = false)
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
            var terminal = updated.IsTerminal;

            // 运行结束后自动拉取断言明细，省掉手动"读取检查点"一步
            if (loadCheckpointAfter && terminal)
                await ReadCheckpointCoreAsync(runId).ConfigureAwait(true);

            if (terminal && !loadCheckpointAfter)
            {
                StatusText = $"{operation}完成：{TranslateRunStatus(updated.StatusText)}，Offset={updated.CurrentOffsetMilliseconds}ms，Timeline={updated.ProgressText}";
            }
            else if (!terminal)
            {
                StatusText = $"运行中：Offset={updated.CurrentOffsetMilliseconds}ms · Timeline={updated.ProgressText}（进度每 1.5 秒自动刷新）";
            }
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

    // ==================== 运行进度自动轮询 ====================

    /// <summary>
    /// 页面初始化时启动 1.5 秒周期的后台轮询：
    /// 仅当存在非终态选中 Run 时才发起 GET runs，终态后自动停止并补拉一次断言。
    /// </summary>
    private void StartRunPolling()
    {
        _runPollCts?.Cancel();
        _runPollCts?.Dispose();
        _runPollCts = new CancellationTokenSource();
        var ignored = PollLoopAsync(_runPollCts.Token);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                await PollSelectedRunOnceAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // 页签关闭或页面释放
        }
        catch (ObjectDisposedException)
        {
            // 页面释放竞态，忽略
        }
    }

    private async Task PollSelectedRunOnceAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed || !CanExecuteSimulation || IsBusy || SelectedRun is null || SelectedRun.IsTerminal)
            return;

        var watchedRunId = SelectedRun.RunId;
        try
        {
            var runs = await _api.GetRunsAsync(cancellationToken).ConfigureAwait(true);
            if (IsDisposed)
                return;

            var current = runs.FirstOrDefault(x => x.RunId == watchedRunId);
            if (current is null)
                return;

            if (!current.IsTerminal)
            {
                // 有变化才重建列表，避免每秒整表刷新导致 DataGrid 抖动
                if (!string.Equals(RunSignature(current), RunSignature(SelectedRun), StringComparison.Ordinal))
                {
                    await RefreshRunsCoreAsync(watchedRunId).ConfigureAwait(true);
                    StatusText = $"运行中：Offset={current.CurrentOffsetMilliseconds}ms · Timeline={current.ProgressText}（自动刷新）";
                }
                return;
            }

            // 刚刚进入终态：最终刷新 + 自动拉取断言明细
            await RefreshRunsCoreAsync(watchedRunId).ConfigureAwait(true);
            try
            {
                await ReadCheckpointCoreAsync(watchedRunId).ConfigureAwait(true);
            }
            catch
            {
                // 已取消的 Run 可能没有可用检查点；不影响终态展示
            }
            StatusText = $"运行结束：{TranslateRunStatus(current.StatusText)}"
                + (string.IsNullOrWhiteSpace(current.FailureMessage) ? string.Empty : $" · {current.FailureMessage}");
        }
        catch
        {
            // 轮询失败保持静默，不打断用户操作
        }
    }

    private static string RunSignature(SimulationRunDto run) =>
        $"{run.StatusText}|{run.CurrentOffsetMilliseconds}|{run.NextTimelineIndex}|{run.FailureMessage}";

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
        ExecutionBoundaryText = "Production/未授权环境 fail-closed。"
            + "启用仿真需在 Host 端 appsettings.json 配置："
            + "\"Simulator\": { \"Enabled\": true } 与 \"SimulationGovernance\": { \"Enabled\": true }，"
            + "并将 ASPNETCORE_ENVIRONMENT 设为 SimulationGovernance:AllowedEnvironments 之一（默认 Simulation / SimulationLoadTest）。";
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
