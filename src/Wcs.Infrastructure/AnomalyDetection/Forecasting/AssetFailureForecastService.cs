namespace Wcs.Infrastructure.AnomalyDetection.Forecasting;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Core.AnomalyDetection.HealthScoring;

public sealed class AssetFailureForecastService : IAssetFailureForecastService, IDisposable
{
    private readonly AssetFailureForecastOptions _options;
    private readonly IAssetFailureForecastModelStore _modelStore;
    private readonly IAssetFailureForecastStore _forecastStore;
    private readonly IAssetHealthScoreHistoryStore _historyStore;
    private readonly IAssetHealthScoringService _healthService;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly object _runtimeGate = new();
    private IAssetFailureForecastRuntime? _runtime;
    private string? _manifestHash;
    private DateTime? _loadedUtc;
    private string? _lastError;
    private long _evaluationAttempts;
    private long _forecastsCreated;
    private long _insufficientData;
    private long _failures;
    private int _initialized;
    private int _disposed;

    public AssetFailureForecastService(
        AssetFailureForecastOptions options,
        IAssetFailureForecastModelStore modelStore,
        IAssetFailureForecastStore forecastStore,
        IAssetHealthScoreHistoryStore historyStore,
        IAssetHealthScoringService healthService)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _forecastStore = forecastStore ?? throw new ArgumentNullException(nameof(forecastStore));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) == 1) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized == 1) return;
            if (_options.Enabled)
                await TryLoadActiveAsync(cancellationToken);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<AssetFailureForecastAttempt> EvaluateAssetAsync(
        string assetId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _evaluationAttempts);
        var normalizedAssetId = assetId?.Trim() ?? string.Empty;
        if (!_options.Enabled)
            return Unavailable(normalizedAssetId, AssetFailureForecastAvailability.Disabled, "Asset failure forecasting is disabled.");
        if (normalizedAssetId.Length == 0)
            return Unavailable(normalizedAssetId, AssetFailureForecastAvailability.InsufficientData, "AssetId is required.");
        if (Volatile.Read(ref _initialized) == 0) await InitializeAsync(cancellationToken);

        IAssetFailureForecastRuntime? runtime;
        string? manifestHash;
        lock (_runtimeGate)
        {
            runtime = _runtime;
            manifestHash = _manifestHash;
        }
        if (runtime is null || manifestHash is null)
            return Unavailable(normalizedAssetId, AssetFailureForecastAvailability.ModelUnavailable, "No approved local forecast model is active.");

        try
        {
            var history = await _historyStore.GetHistoryAsync(
                normalizedAssetId,
                fromUtc: null,
                maximumCount: _options.MaximumHistoryPoints,
                cancellationToken);
            if (!AssetFailureForecastFeatureBuilder.TryBuild(
                    normalizedAssetId,
                    history,
                    _options,
                    out var vector,
                    out var reason))
            {
                Interlocked.Increment(ref _insufficientData);
                return Unavailable(normalizedAssetId, AssetFailureForecastAvailability.InsufficientData, reason);
            }

            AssetFailureForecastOutput output;
            lock (_runtimeGate)
            {
                if (!ReferenceEquals(runtime, _runtime))
                    throw new InvalidOperationException("Forecast model changed during evaluation; retry with the active version.");
                output = runtime.Predict(vector!);
            }
            var current = history[^1];
            var forecastedAt = utcNow == default ? DateTime.UtcNow : utcNow;
            var forecast = new AssetFailureForecastPrediction
            {
                ForecastId = AssetFailureForecastIdentity.CreateForecastId(
                    normalizedAssetId,
                    runtime.Manifest.Version,
                    vector!.WindowStartUtc,
                    vector.WindowEndUtc),
                AssetId = normalizedAssetId,
                ModelVersion = runtime.Manifest.Version,
                ManifestHash = manifestHash,
                WindowStartUtc = vector.WindowStartUtc,
                WindowEndUtc = vector.WindowEndUtc,
                ForecastedAtUtc = forecastedAt,
                SampleCount = vector.SampleCount,
                HistorySpanHours = vector.HistorySpanHours,
                FailureProbability24Hours = output.FailureProbability24Hours,
                FailureProbability72Hours = output.FailureProbability72Hours,
                FailureProbability168Hours = output.FailureProbability168Hours,
                RulLowerHours = output.RulLowerHours,
                RulMedianHours = output.RulMedianHours,
                RulUpperHours = output.RulUpperHours,
                CurrentHealthScore = current.HealthScore,
                CurrentGrade = current.Grade,
                Explanation = BuildExplanation(runtime.Manifest, vector, output)
            };
            if (await _forecastStore.SaveForecastAsync(forecast, cancellationToken))
                Interlocked.Increment(ref _forecastsCreated);
            ClearError();
            return new AssetFailureForecastAttempt
            {
                AssetId = normalizedAssetId,
                Availability = AssetFailureForecastAvailability.Ready,
                Prediction = forecast
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            return Unavailable(normalizedAssetId, AssetFailureForecastAvailability.Failed, exception.Message);
        }
    }

    public async Task<int> EvaluateAllAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_options.Enabled) return 0;
        var assets = _healthService.GetAssets(
            minimumGrade: null,
            maximumCount: _options.MaximumAssetsPerEvaluation);
        var created = 0;
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = await EvaluateAssetAsync(asset.AssetId, utcNow, cancellationToken);
            if (attempt.Prediction is not null) created++;
        }
        return created;
    }

    public Task<AssetFailureForecastPrediction?> GetLatestAsync(
        string assetId,
        CancellationToken cancellationToken = default) =>
        _forecastStore.GetLatestAsync(assetId, cancellationToken);

    public Task<IReadOnlyList<AssetFailureForecastPrediction>> QueryAsync(
        string? assetId,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        _forecastStore.QueryAsync(assetId, maximumCount, cancellationToken);

    public Task<IReadOnlyList<AssetFailureForecastModelManifest>> ListModelsAsync(
        CancellationToken cancellationToken = default) =>
        _modelStore.ListAsync(cancellationToken);

    public async Task ActivateModelAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_options.Enabled)
            throw new InvalidOperationException("Asset failure forecasting is disabled.");
        var artifact = await _modelStore.LoadVersionAsync(version, cancellationToken)
            ?? throw new KeyNotFoundException($"Failure forecast model was not found: {version}.");
        var loaded = CreateRuntime(artifact);
        var hash = AssetFailureForecastManifestValidator.ComputeManifestHash(artifact.Manifest);
        try
        {
            await _forecastStore.EnsureModelVersionAsync(artifact.Manifest, hash, cancellationToken);
            await _modelStore.ActivateAsync(version, cancellationToken);
            SwapRuntime(loaded, hash);
            ClearError();
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    public Task<bool> AppendOutcomeAsync(
        AssetFailureForecastOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (string.IsNullOrWhiteSpace(outcome.ForecastId) ||
            string.IsNullOrWhiteSpace(outcome.RecordedBy) ||
            string.IsNullOrWhiteSpace(outcome.Note) ||
            outcome.ObservedAtUtc == default)
            throw new InvalidOperationException("ForecastId, ObservedAtUtc, RecordedBy and Note are required.");
        if (!Enum.IsDefined(outcome.Kind))
            throw new InvalidOperationException("Forecast outcome kind is invalid.");
        return _forecastStore.AppendOutcomeAsync(outcome, cancellationToken);
    }

    public Task<IReadOnlyList<AssetFailureForecastOutcome>> GetOutcomesAsync(
        string forecastId,
        CancellationToken cancellationToken = default) =>
        _forecastStore.GetOutcomesAsync(forecastId, cancellationToken);

    public Task<AssetFailureForecastMetrics> GetMetricsAsync(
        CancellationToken cancellationToken = default) =>
        _forecastStore.GetMetricsAsync(cancellationToken);

    public AssetFailureForecastStatus GetStatus()
    {
        IAssetFailureForecastRuntime? runtime;
        string? manifestHash;
        DateTime? loadedUtc;
        string? lastError;
        lock (_runtimeGate)
        {
            runtime = _runtime;
            manifestHash = _manifestHash;
            loadedUtc = _loadedUtc;
            lastError = _lastError;
        }
        var availability = !_options.Enabled
            ? AssetFailureForecastAvailability.Disabled
            : runtime is not null
                ? AssetFailureForecastAvailability.Ready
                : lastError is null
                    ? AssetFailureForecastAvailability.ModelUnavailable
                    : AssetFailureForecastAvailability.Failed;
        return new AssetFailureForecastStatus
        {
            Enabled = _options.Enabled,
            Availability = availability,
            ActiveModelVersion = runtime?.Manifest.Version,
            ManifestHash = manifestHash,
            ArtifactSha256 = runtime?.Manifest.ArtifactSha256,
            EvaluationAttempts = Interlocked.Read(ref _evaluationAttempts),
            ForecastsCreated = Interlocked.Read(ref _forecastsCreated),
            InsufficientData = Interlocked.Read(ref _insufficientData),
            Failures = Interlocked.Read(ref _failures),
            LoadedUtc = loadedUtc,
            LastError = lastError
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IAssetFailureForecastRuntime? runtime;
        lock (_runtimeGate)
        {
            runtime = _runtime;
            _runtime = null;
        }
        runtime?.Dispose();
        _initializeLock.Dispose();
    }

    private async Task TryLoadActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var artifact = await _modelStore.LoadActiveAsync(cancellationToken);
            if (artifact is null)
            {
                ClearError();
                return;
            }
            var runtime = CreateRuntime(artifact);
            var hash = AssetFailureForecastManifestValidator.ComputeManifestHash(artifact.Manifest);
            await _forecastStore.EnsureModelVersionAsync(artifact.Manifest, hash, cancellationToken);
            SwapRuntime(runtime, hash);
            ClearError();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
    }

    private IAssetFailureForecastRuntime CreateRuntime(AssetFailureForecastModelArtifact artifact)
    {
        AssetFailureForecastManifestValidator.Validate(artifact.Manifest, _options);
        return new OnnxAssetFailureForecastRuntime(artifact, _options);
    }

    private void SwapRuntime(IAssetFailureForecastRuntime runtime, string manifestHash)
    {
        IAssetFailureForecastRuntime? previous;
        lock (_runtimeGate)
        {
            previous = _runtime;
            _runtime = runtime;
            _manifestHash = manifestHash;
            _loadedUtc = DateTime.UtcNow;
        }
        previous?.Dispose();
    }

    private void RecordFailure(Exception exception)
    {
        Interlocked.Increment(ref _failures);
        lock (_runtimeGate) _lastError = exception.Message;
    }

    private void ClearError()
    {
        lock (_runtimeGate) _lastError = null;
    }

    private static AssetFailureForecastAttempt Unavailable(
        string assetId,
        AssetFailureForecastAvailability availability,
        string? reason) => new()
    {
        AssetId = assetId,
        Availability = availability,
        Reason = reason
    };

    private static string BuildExplanation(
        AssetFailureForecastModelManifest manifest,
        AssetFailureForecastFeatureVector vector,
        AssetFailureForecastOutput output) =>
        $"Approved local model {manifest.Version} on dataset {manifest.TrainingDatasetVersion}; " +
        $"history={vector.SampleCount} points/{vector.HistorySpanHours:F1}h; " +
        $"failure probability 24h={output.FailureProbability24Hours:P1}, 72h={output.FailureProbability72Hours:P1}, 168h={output.FailureProbability168Hours:P1}; " +
        $"RUL interval={output.RulLowerHours:F1}-{output.RulUpperHours:F1}h, median={output.RulMedianHours:F1}h. " +
        "Diagnostic estimate only; not a safety interlock or maintenance command.";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}

public sealed class AssetFailureForecastBackgroundService : BackgroundService
{
    private readonly IAssetFailureForecastService _service;
    private readonly IAssetFailureForecastStore _store;
    private readonly AssetFailureForecastOptions _options;
    private readonly ILogger<AssetFailureForecastBackgroundService> _logger;

    public AssetFailureForecastBackgroundService(
        IAssetFailureForecastService service,
        IAssetFailureForecastStore store,
        AssetFailureForecastOptions options,
        ILogger<AssetFailureForecastBackgroundService> logger)
    {
        _service = service;
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _service.InitializeAsync(stoppingToken);
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.EvaluationIntervalSeconds));
        var nextMaintenanceUtc = DateTime.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTime.UtcNow;
                await _service.EvaluateAllAsync(now, stoppingToken);
                if (now >= nextMaintenanceUtc)
                {
                    await _store.MaintainAsync(now, stoppingToken);
                    nextMaintenanceUtc = now.AddSeconds(_options.MaintenanceIntervalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Asset failure forecast cycle failed outside the real-time control path.");
            }
        }
    }
}
