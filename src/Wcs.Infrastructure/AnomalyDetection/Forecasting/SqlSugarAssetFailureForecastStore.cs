namespace Wcs.Infrastructure.AnomalyDetection.Forecasting;

using System.Text.Json;
using SqlSugar;
using Wcs.Core.AnomalyDetection.Forecasting;
using Wcs.Core.AnomalyDetection.HealthScoring;

[SugarTable("Wcs_AssetFailureForecastModelVersion")]
public sealed class AssetFailureForecastModelVersionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }
    [SugarColumn(Length = 128)]
    public string Version { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string ManifestHash { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string ArtifactSha256 { get; set; } = string.Empty;
    [SugarColumn(Length = 256)]
    public string TrainingDatasetVersion { get; set; } = string.Empty;
    public int TrainingAssetCount { get; set; }
    public int FailureEventCount { get; set; }
    public int CensoredRecordCount { get; set; }
    public double ValidationAuc { get; set; }
    public double ValidationBrierScore { get; set; }
    public double ValidationRulMaeHours { get; set; }
    public double ValidationIntervalCoverage { get; set; }
    public DateTime CreatedUtc { get; set; }
    [SugarColumn(Length = 512)]
    public string Source { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime ApprovedAtUtc { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string ManifestJson { get; set; } = string.Empty;
    public DateTime RegisteredAtUtc { get; set; }
}

[SugarTable("Wcs_AssetFailureForecast")]
public sealed class AssetFailureForecastEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }
    [SugarColumn(Length = 64)]
    public string ForecastId { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string AssetId { get; set; } = string.Empty;
    [SugarColumn(Length = 128)]
    public string ModelVersion { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string ManifestHash { get; set; } = string.Empty;
    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }
    public DateTime ForecastedAtUtc { get; set; }
    public int SampleCount { get; set; }
    public double HistorySpanHours { get; set; }
    public double FailureProbability24Hours { get; set; }
    public double FailureProbability72Hours { get; set; }
    public double FailureProbability168Hours { get; set; }
    public double RulLowerHours { get; set; }
    public double RulMedianHours { get; set; }
    public double RulUpperHours { get; set; }
    public double CurrentHealthScore { get; set; }
    public int CurrentGrade { get; set; }
    [SugarColumn(Length = 2000)]
    public string Explanation { get; set; } = string.Empty;
}

[SugarTable("Wcs_AssetFailureForecastOutcomeJournal")]
public sealed class AssetFailureForecastOutcomeEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }
    [SugarColumn(Length = 64)]
    public string OutcomeId { get; set; } = string.Empty;
    [SugarColumn(Length = 64)]
    public string ForecastId { get; set; } = string.Empty;
    public int Kind { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    [SugarColumn(Length = 128)]
    public string RecordedBy { get; set; } = string.Empty;
    [SugarColumn(Length = 2000)]
    public string Note { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; }
}

public sealed class SqlSugarAssetFailureForecastStore : IAssetFailureForecastStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly AssetFailureForecastOptions _options;

    public SqlSugarAssetFailureForecastStore(string connectionString, AssetFailureForecastOptions options)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("WcsDb connection string is required.", nameof(connectionString))
            : connectionString;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task EnsureModelVersionAsync(
        AssetFailureForecastModelManifest manifest,
        string manifestHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var existing = db.Queryable<AssetFailureForecastModelVersionEntity>()
            .First(row => row.Version == manifest.Version);
        if (existing is not null)
        {
            if (!string.Equals(existing.ManifestHash, manifestHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Failure forecast model Version {manifest.Version} is already registered with a different ManifestHash.");
            return Task.CompletedTask;
        }

        db.Insertable(new AssetFailureForecastModelVersionEntity
        {
            Version = manifest.Version,
            ManifestHash = manifestHash,
            ArtifactSha256 = manifest.ArtifactSha256.ToUpperInvariant(),
            TrainingDatasetVersion = manifest.TrainingDatasetVersion,
            TrainingAssetCount = manifest.TrainingAssetCount,
            FailureEventCount = manifest.FailureEventCount,
            CensoredRecordCount = manifest.CensoredRecordCount,
            ValidationAuc = manifest.ValidationAuc,
            ValidationBrierScore = manifest.ValidationBrierScore,
            ValidationRulMaeHours = manifest.ValidationRulMaeHours,
            ValidationIntervalCoverage = manifest.ValidationIntervalCoverage,
            CreatedUtc = manifest.CreatedUtc,
            Source = manifest.Source,
            ApprovedBy = manifest.ApprovedBy,
            ApprovedAtUtc = manifest.ApprovedAtUtc!.Value,
            ManifestJson = JsonSerializer.Serialize(manifest, JsonOptions),
            RegisteredAtUtc = DateTime.UtcNow
        }).ExecuteCommand();
        return Task.CompletedTask;
    }

    public Task<bool> SaveForecastAsync(
        AssetFailureForecastPrediction forecast,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        if (db.Queryable<AssetFailureForecastEntity>().Any(row => row.ForecastId == forecast.ForecastId))
            return Task.FromResult(false);
        try
        {
            db.Insertable(ToEntity(forecast)).ExecuteCommand();
            return Task.FromResult(true);
        }
        catch (SqlSugarException)
        {
            using var verify = CreateClient();
            if (verify.Queryable<AssetFailureForecastEntity>().Any(row => row.ForecastId == forecast.ForecastId))
                return Task.FromResult(false);
            throw;
        }
    }

    public Task<AssetFailureForecastPrediction?> GetLatestAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = assetId?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return Task.FromResult<AssetFailureForecastPrediction?>(null);
        using var db = CreateClient();
        var row = db.Queryable<AssetFailureForecastEntity>()
            .Where(item => item.AssetId == normalized)
            .OrderBy(item => item.Sequence, OrderByType.Desc)
            .First();
        return Task.FromResult(row is null ? null : ToModel(row));
    }

    public Task<IReadOnlyList<AssetFailureForecastPrediction>> QueryAsync(
        string? assetId,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumForecastsQueryCount);
        using var db = CreateClient();
        var query = db.Queryable<AssetFailureForecastEntity>();
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            var normalized = assetId.Trim();
            query = query.Where(row => row.AssetId == normalized);
        }
        IReadOnlyList<AssetFailureForecastPrediction> result = query
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(maximumCount)
            .ToList()
            .Select(ToModel)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<AssetFailureForecastOutcome>> GetOutcomesAsync(
        string forecastId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        IReadOnlyList<AssetFailureForecastOutcome> result = db.Queryable<AssetFailureForecastOutcomeEntity>()
            .Where(row => row.ForecastId == forecastId)
            .OrderBy(row => row.Sequence, OrderByType.Asc)
            .ToList()
            .Select(ToModel)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<bool> AppendOutcomeAsync(
        AssetFailureForecastOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        if (!db.Queryable<AssetFailureForecastEntity>().Any(row => row.ForecastId == outcome.ForecastId))
            throw new KeyNotFoundException($"Forecast was not found: {outcome.ForecastId}.");
        if (db.Queryable<AssetFailureForecastOutcomeEntity>().Any(row => row.OutcomeId == outcome.OutcomeId))
            return Task.FromResult(false);
        db.Insertable(new AssetFailureForecastOutcomeEntity
        {
            OutcomeId = outcome.OutcomeId,
            ForecastId = outcome.ForecastId,
            Kind = (int)outcome.Kind,
            ObservedAtUtc = outcome.ObservedAtUtc,
            RecordedBy = outcome.RecordedBy,
            Note = outcome.Note,
            RecordedAtUtc = DateTime.UtcNow
        }).ExecuteCommand();
        return Task.FromResult(true);
    }

    public Task<AssetFailureForecastMetrics> GetMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var forecasts = db.Queryable<AssetFailureForecastEntity>().ToList();
        var outcomes = db.Queryable<AssetFailureForecastOutcomeEntity>().ToList();
        var joined = (from outcome in outcomes
                      join forecast in forecasts on outcome.ForecastId equals forecast.ForecastId
                      select new { outcome, forecast }).ToArray();
        var failures = joined.Where(item => item.outcome.Kind == (int)AssetFailureForecastOutcomeKind.ObservedFailure).ToArray();
        var rulErrors = failures
            .Select(item => Math.Abs(
                (item.outcome.ObservedAtUtc - item.forecast.ForecastedAtUtc).TotalHours -
                item.forecast.RulMedianHours))
            .Where(double.IsFinite)
            .ToArray();
        var coverage = failures
            .Select(item => (item.outcome.ObservedAtUtc - item.forecast.ForecastedAtUtc).TotalHours)
            .Select((actual, index) => actual >= failures[index].forecast.RulLowerHours && actual <= failures[index].forecast.RulUpperHours ? 1d : 0d)
            .ToArray();
        var brierSamples = joined
            .Where(item => item.outcome.Kind is
                (int)AssetFailureForecastOutcomeKind.ObservedFailure or
                (int)AssetFailureForecastOutcomeKind.CensoredNoFailure)
            .Select(item =>
            {
                var observed = item.outcome.Kind == (int)AssetFailureForecastOutcomeKind.ObservedFailure &&
                    item.outcome.ObservedAtUtc <= item.forecast.ForecastedAtUtc.AddHours(24)
                    ? 1d
                    : 0d;
                return Math.Pow(item.forecast.FailureProbability24Hours - observed, 2);
            })
            .ToArray();
        return Task.FromResult(new AssetFailureForecastMetrics
        {
            ForecastCount = forecasts.Count,
            OutcomeCount = outcomes.Count,
            ObservedFailureCount = failures.Length,
            MeanAbsoluteRulErrorHours = rulErrors.Length == 0 ? null : rulErrors.Average(),
            PredictionIntervalCoverage = coverage.Length == 0 ? null : coverage.Average(),
            BrierScore24Hours = brierSamples.Length == 0 ? null : brierSamples.Average()
        });
    }

    public Task MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateClient();
        var cutoff = utcNow.AddHours(-_options.ForecastRetentionHours);
        var ids = db.Queryable<AssetFailureForecastEntity>()
            .Where(row => row.ForecastedAtUtc < cutoff)
            .OrderBy(row => row.Sequence, OrderByType.Asc)
            .Take(_options.MaintenanceBatchSize)
            .Select(row => row.ForecastId)
            .ToList();
        if (ids.Count == 0) return Task.CompletedTask;
        db.Deleteable<AssetFailureForecastOutcomeEntity>()
            .Where(row => ids.Contains(row.ForecastId))
            .ExecuteCommand();
        db.Deleteable<AssetFailureForecastEntity>()
            .Where(row => ids.Contains(row.ForecastId))
            .ExecuteCommand();
        return Task.CompletedTask;
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private static AssetFailureForecastEntity ToEntity(AssetFailureForecastPrediction model) => new()
    {
        ForecastId = model.ForecastId,
        AssetId = model.AssetId,
        ModelVersion = model.ModelVersion,
        ManifestHash = model.ManifestHash,
        WindowStartUtc = model.WindowStartUtc,
        WindowEndUtc = model.WindowEndUtc,
        ForecastedAtUtc = model.ForecastedAtUtc,
        SampleCount = model.SampleCount,
        HistorySpanHours = model.HistorySpanHours,
        FailureProbability24Hours = model.FailureProbability24Hours,
        FailureProbability72Hours = model.FailureProbability72Hours,
        FailureProbability168Hours = model.FailureProbability168Hours,
        RulLowerHours = model.RulLowerHours,
        RulMedianHours = model.RulMedianHours,
        RulUpperHours = model.RulUpperHours,
        CurrentHealthScore = model.CurrentHealthScore,
        CurrentGrade = (int)model.CurrentGrade,
        Explanation = model.Explanation
    };

    private static AssetFailureForecastPrediction ToModel(AssetFailureForecastEntity row) => new()
    {
        ForecastId = row.ForecastId,
        AssetId = row.AssetId,
        ModelVersion = row.ModelVersion,
        ManifestHash = row.ManifestHash,
        WindowStartUtc = row.WindowStartUtc,
        WindowEndUtc = row.WindowEndUtc,
        ForecastedAtUtc = row.ForecastedAtUtc,
        SampleCount = row.SampleCount,
        HistorySpanHours = row.HistorySpanHours,
        FailureProbability24Hours = row.FailureProbability24Hours,
        FailureProbability72Hours = row.FailureProbability72Hours,
        FailureProbability168Hours = row.FailureProbability168Hours,
        RulLowerHours = row.RulLowerHours,
        RulMedianHours = row.RulMedianHours,
        RulUpperHours = row.RulUpperHours,
        CurrentHealthScore = row.CurrentHealthScore,
        CurrentGrade = (AssetHealthGrade)row.CurrentGrade,
        Explanation = row.Explanation
    };

    private static AssetFailureForecastOutcome ToModel(AssetFailureForecastOutcomeEntity row) => new()
    {
        OutcomeId = row.OutcomeId,
        ForecastId = row.ForecastId,
        Kind = (AssetFailureForecastOutcomeKind)row.Kind,
        ObservedAtUtc = row.ObservedAtUtc,
        RecordedBy = row.RecordedBy,
        Note = row.Note
    };
}
