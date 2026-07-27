namespace Wcs.Core.AnomalyDetection.HealthScoring;

using Wcs.Core.AnomalyDetection.Fusion;

/// <summary>
/// 将 v3.3 的融合风险快照转换为 0-100 健康分。
/// 健康分仅用于诊断展示，不参与 PLC 写入、设备停机或调度决策。
/// </summary>
public sealed class AssetHealthScoringService : IAssetHealthScoringService
{
    private readonly AssetHealthScoringOptions _options;
    private readonly AnomalyFusionOptions _fusionOptions;
    private readonly IAnomalyFusionEngine _fusionEngine;

    public AssetHealthScoringService(
        AssetHealthScoringOptions options,
        AnomalyFusionOptions fusionOptions,
        IAnomalyFusionEngine fusionEngine)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fusionOptions = fusionOptions ?? throw new ArgumentNullException(nameof(fusionOptions));
        _fusionEngine = fusionEngine ?? throw new ArgumentNullException(nameof(fusionEngine));
    }

    public AssetHealthScoreSnapshot? Evaluate(FusedHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_options.Enabled) return null;

        var risk = Math.Clamp(snapshot.Score, 0, 1);
        var healthScore = Math.Round(MapRiskToHealthScore(risk), 2, MidpointRounding.AwayFromZero);
        var grade = ResolveGrade(healthScore, snapshot.Status);
        var totalPenalty = Math.Max(0, 100 - healthScore);

        var evidence = snapshot.Evidence
            .OrderByDescending(static item => item.Contribution)
            .ThenByDescending(static item => item.ObservedAtUtc)
            .Take(_options.MaximumFactors)
            .ToArray();
        var contributionTotal = evidence.Sum(static item => Math.Max(0, item.Contribution));
        var factors = evidence
            .Select(item => new AssetHealthFactor
            {
                Source = item.Source,
                Category = item.Category,
                Contribution = Math.Round(item.Contribution, 4, MidpointRounding.AwayFromZero),
                Penalty = contributionTotal <= 0
                    ? 0
                    : Math.Round(
                        totalPenalty * Math.Max(0, item.Contribution) / contributionTotal,
                        2,
                        MidpointRounding.AwayFromZero),
                Reason = item.Reason
            })
            .ToArray();

        var summary = factors.Length == 0
            ? $"健康分 {healthScore:F2}，当前无活动异常证据。"
            : $"健康分 {healthScore:F2}，主要影响：{factors[0].Source}/{factors[0].Category}，扣分 {factors[0].Penalty:F2}。";

        return new AssetHealthScoreSnapshot
        {
            AssetId = snapshot.AssetId,
            HealthScore = healthScore,
            Grade = grade,
            FusionRiskScore = risk,
            FusionStatus = snapshot.Status,
            IndependentSourceCount = snapshot.IndependentSourceCount,
            CalculatedAtUtc = snapshot.LastEvaluatedAtUtc,
            Factors = factors,
            Summary = summary
        };
    }

    public AssetHealthScoreSnapshot? GetAsset(string assetId)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(assetId)) return null;
        var snapshot = _fusionEngine.GetAsset(assetId.Trim());
        return snapshot is null ? null : Evaluate(snapshot);
    }

    public IReadOnlyList<AssetHealthScoreSnapshot> GetAssets(
        AssetHealthGrade? minimumGrade = null,
        int maximumCount = 200)
    {
        if (!_options.Enabled) return Array.Empty<AssetHealthScoreSnapshot>();

        maximumCount = Math.Clamp(maximumCount, 1, 5_000);
        return _fusionEngine
            .GetAssets(minimumStatus: null, maximumCount: 10_000)
            .Select(Evaluate)
            .Where(static snapshot => snapshot is not null)
            .Select(static snapshot => snapshot!)
            .Where(snapshot => minimumGrade is null || snapshot.Grade >= minimumGrade.Value)
            .OrderByDescending(static snapshot => snapshot.Grade)
            .ThenBy(static snapshot => snapshot.HealthScore)
            .ThenBy(static snapshot => snapshot.AssetId, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
    }

    public AssetHealthScoringStatus GetStatus()
    {
        var fusionStatus = _fusionEngine.GetStatus();
        return new AssetHealthScoringStatus
        {
            Enabled = _options.Enabled,
            FusionEnabled = fusionStatus.Enabled,
            TrackedAssets = fusionStatus.TrackedAssets,
            HealthyMinimumScore = _options.HealthyMinimumScore,
            AttentionMinimumScore = _options.AttentionMinimumScore,
            DegradedMinimumScore = _options.DegradedMinimumScore,
            MaximumFactors = _options.MaximumFactors
        };
    }

    private double MapRiskToHealthScore(double risk)
    {
        var observe = Math.Clamp(_fusionOptions.ObserveThreshold, 0, 1);
        var warning = Math.Clamp(Math.Max(_fusionOptions.WarningThreshold, observe), 0, 1);
        var alarm = Math.Clamp(Math.Max(_fusionOptions.AlarmThreshold, warning), 0, 1);

        if (risk <= observe)
            return Lerp(100, _options.HealthyMinimumScore, Ratio(risk, observe));
        if (risk <= warning)
            return Lerp(
                _options.HealthyMinimumScore,
                _options.AttentionMinimumScore,
                Ratio(risk - observe, warning - observe));
        if (risk <= alarm)
            return Lerp(
                _options.AttentionMinimumScore,
                _options.DegradedMinimumScore,
                Ratio(risk - warning, alarm - warning));

        return Lerp(
            _options.DegradedMinimumScore,
            0,
            Ratio(risk - alarm, 1 - alarm));
    }

    private AssetHealthGrade ResolveGrade(double healthScore, FusedHealthStatus fusionStatus)
    {
        var scoreGrade = healthScore >= _options.HealthyMinimumScore
            ? AssetHealthGrade.Healthy
            : healthScore >= _options.AttentionMinimumScore
                ? AssetHealthGrade.Attention
                : healthScore >= _options.DegradedMinimumScore
                    ? AssetHealthGrade.Degraded
                    : AssetHealthGrade.Critical;

        var statusGrade = fusionStatus switch
        {
            FusedHealthStatus.Normal => AssetHealthGrade.Healthy,
            FusedHealthStatus.Observe => AssetHealthGrade.Attention,
            FusedHealthStatus.Warning => AssetHealthGrade.Degraded,
            FusedHealthStatus.Alarm => AssetHealthGrade.Critical,
            _ => AssetHealthGrade.Healthy
        };

        return (AssetHealthGrade)Math.Max((int)scoreGrade, (int)statusGrade);
    }

    private static double Ratio(double numerator, double denominator) =>
        denominator <= 0 ? 1 : Math.Clamp(numerator / denominator, 0, 1);

    private static double Lerp(double start, double end, double ratio) =>
        start + ((end - start) * Math.Clamp(ratio, 0, 1));
}
