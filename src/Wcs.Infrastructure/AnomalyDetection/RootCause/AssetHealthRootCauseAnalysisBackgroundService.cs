namespace Wcs.Infrastructure.AnomalyDetection.RootCause;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.RootCause;

/// <summary>
/// 周期读取 v3.5 活动健康事件并生成 v3.6 根因分析快照。
/// SQL 或分析异常只影响诊断结果，不阻塞 PLC、任务和调度链路。
/// </summary>
public sealed class AssetHealthRootCauseAnalysisBackgroundService : BackgroundService,
    IAssetHealthRootCauseRuntimeStatus
{
    private readonly AssetHealthRootCauseOptions _options;
    private readonly AssetHealthGovernanceOptions _governanceOptions;
    private readonly IAssetHealthGovernanceService _governance;
    private readonly IAssetHealthRootCauseAnalysisEngine _engine;
    private readonly IAssetHealthRootCauseAnalysisStore _store;
    private readonly ILogger<AssetHealthRootCauseAnalysisBackgroundService> _logger;
    private readonly object _statusGate = new();
    private DateTime? _lastAnalysisUtc;
    private string? _lastError;

    public AssetHealthRootCauseAnalysisBackgroundService(
        AssetHealthRootCauseOptions options,
        AssetHealthGovernanceOptions governanceOptions,
        IAssetHealthGovernanceService governance,
        IAssetHealthRootCauseAnalysisEngine engine,
        IAssetHealthRootCauseAnalysisStore store,
        ILogger<AssetHealthRootCauseAnalysisBackgroundService> logger)
    {
        _options = options;
        _governanceOptions = governanceOptions;
        _governance = governance;
        _engine = engine;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Asset health root cause analysis is disabled.");
            return;
        }

        await InitializeAsync(stoppingToken);
        if (!_governanceOptions.Enabled)
        {
            _logger.LogWarning(
                "Asset health root cause analysis is enabled while AssetHealthGovernance is disabled; no active events will be analyzed.");
        }

        _logger.LogInformation(
            "Asset health root cause analysis started. GraphVersion={GraphVersion}, Nodes={Nodes}, Edges={Edges}, IntervalSeconds={IntervalSeconds}",
            _engine.GraphRegistration.Version,
            _engine.GraphRegistration.NodeCount,
            _engine.GraphRegistration.EdgeCount,
            _options.EvaluationIntervalSeconds);

        await AnalyzeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await AnalyzeAsync(stoppingToken);
    }

    public AssetHealthRootCauseStatus GetStatus()
    {
        DateTime? lastAnalysis;
        string? lastError;
        lock (_statusGate)
        {
            lastAnalysis = _lastAnalysisUtc;
            lastError = _lastError;
        }
        var registration = _engine.GraphRegistration;
        return new AssetHealthRootCauseStatus
        {
            Enabled = _options.Enabled,
            GovernanceEnabled = _governanceOptions.Enabled,
            GraphValid = true,
            GraphVersion = registration.Version,
            GraphHash = registration.GraphHash,
            GraphNodes = registration.NodeCount,
            GraphEdges = registration.EdgeCount,
            AllowCycles = _options.AllowCycles,
            CorrelationWindowSeconds = _options.CorrelationWindowSeconds,
            MaximumPropagationDepth = _options.MaximumPropagationDepth,
            MaximumCandidates = _options.MaximumCandidates,
            LastAnalysisUtc = lastAnalysis,
            LastError = lastError
        };
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _store.InitializeAsync(cancellationToken);
                await _store.RegisterGraphAsync(_engine.GraphRegistration, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
                _logger.LogWarning(exception, "Asset health root cause initialization failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            var active = _governance.GetEvents(
                AssetHealthEventLifecycleStatus.Active,
                minimumGrade: null,
                maximumCount: _options.MaximumEventsPerAnalysis);
            foreach (var trigger in active)
            {
                var analysis = _engine.Analyze(trigger, active, utcNow);
                if (analysis is null) continue;
                if (await _store.SaveAsync(analysis, cancellationToken))
                {
                    lock (_statusGate) _lastAnalysisUtc = utcNow;
                }
            }

            await _store.MaintainAsync(utcNow, cancellationToken);
            lock (_statusGate) _lastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
            _logger.LogError(exception, "Asset health root cause analysis cycle failed.");
        }
    }

    private void SetError(string error)
    {
        lock (_statusGate) _lastError = error;
    }
}
