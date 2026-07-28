namespace Wcs.Infrastructure.AnomalyDetection.HealthGovernance;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;

/// <summary>
/// 从 SQL Outbox 拉取待发送健康事件并通过 HTTP 幂等推送 MES。
/// MES 或网络故障只影响诊断通知，不阻塞 PLC、任务和调度。
/// </summary>
public sealed class AssetHealthMesDeliveryService : BackgroundService
{
    public const string HttpClientName = "AssetHealthMes";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AssetHealthGovernanceOptions _options;
    private readonly IAssetHealthEventJournalStore _journal;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AssetHealthMesDeliveryService> _logger;

    public AssetHealthMesDeliveryService(
        AssetHealthGovernanceOptions options,
        IAssetHealthEventJournalStore journal,
        IHttpClientFactory httpClientFactory,
        ILogger<AssetHealthMesDeliveryService> logger)
    {
        _options = options;
        _journal = journal;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.MesPushEnabled)
        {
            _logger.LogInformation("Asset health MES delivery is disabled.");
            return;
        }

        if (!TryBuildEndpoint(out var endpoint))
            throw new InvalidOperationException("AssetHealthGovernance MES endpoint is invalid.");

        await _journal.InitializeAsync(stoppingToken);
        _logger.LogInformation(
            "Asset health MES delivery started. Endpoint={Endpoint}, BatchSize={BatchSize}, MaximumAttempts={MaximumAttempts}",
            endpoint,
            _options.MesBatchSize,
            _options.MesMaximumAttempts);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.MesPollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            await DeliverDueAsync(endpoint, stoppingToken);
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    private async Task DeliverDueAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        IReadOnlyList<AssetHealthEventTransition> pending;
        try
        {
            pending = await _journal.GetPendingDeliveriesAsync(
                DateTime.UtcNow,
                _options.MesBatchSize,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to read asset health MES outbox.");
            return;
        }

        foreach (var transition in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await DeliverOneAsync(endpoint, transition, cancellationToken);
        }
    }

    private async Task DeliverOneAsync(
        Uri endpoint,
        AssetHealthEventTransition transition,
        CancellationToken cancellationToken)
    {
        var attempt = transition.DeliveryAttemptCount + 1;
        int? statusCode = null;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Idempotency-Key", transition.MessageId);
            request.Headers.TryAddWithoutValidation("X-WCS-Event-Id", transition.Event.EventId);
            request.Headers.TryAddWithoutValidation("X-WCS-Event-Version", transition.Event.Version.ToString());
            if (!string.IsNullOrWhiteSpace(_options.MesApiKeyHeader) &&
                !string.IsNullOrWhiteSpace(_options.MesApiKey))
            {
                request.Headers.TryAddWithoutValidation(
                    _options.MesApiKeyHeader.Trim(),
                    _options.MesApiKey);
            }

            var payload = new AssetHealthMesPayload
            {
                MessageId = transition.MessageId,
                EventId = transition.Event.EventId,
                EventVersion = transition.Event.Version,
                Transition = transition.TransitionType,
                EventKey = transition.Event.EventKey,
                AssetId = transition.Event.AssetId,
                LifecycleStatus = transition.Event.LifecycleStatus,
                Grade = transition.Event.Grade,
                PeakGrade = transition.Event.PeakGrade,
                HealthScore = transition.Event.HealthScore,
                LowestHealthScore = transition.Event.LowestHealthScore,
                FirstDetectedUtc = transition.Event.FirstDetectedUtc,
                LastObservedUtc = transition.Event.LastObservedUtc,
                RecoveredAtUtc = transition.Event.RecoveredAtUtc,
                Acknowledged = transition.Event.Acknowledged,
                AcknowledgedAtUtc = transition.Event.AcknowledgedAtUtc,
                AcknowledgedBy = transition.Event.AcknowledgedBy,
                IsSuppressed = transition.Event.IsSuppressed,
                SuppressedUntilUtc = transition.Event.SuppressedUntilUtc,
                SuppressedReason = transition.Event.SuppressedReason,
                Reason = transition.Event.Reason,
                Source = transition.Event.Source,
                Category = transition.Event.Category,
                OccurredAtUtc = transition.OccurredAtUtc,
                Actor = transition.Actor,
                Note = transition.Note,
                SourceSystem = "WCS"
            };
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.MesTimeoutSeconds));
            using var response = await client.SendAsync(request, timeout.Token);
            statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                await _journal.MarkDeliveredAsync(
                    transition.MessageId,
                    DateTime.UtcNow,
                    statusCode,
                    cancellationToken);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            await MarkFailureAsync(
                transition,
                attempt,
                statusCode,
                $"MES returned HTTP {statusCode}: {Truncate(body, 1000)}",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await MarkFailureAsync(
                transition,
                attempt,
                statusCode,
                exception.Message,
                cancellationToken);
        }
    }

    private async Task MarkFailureAsync(
        AssetHealthEventTransition transition,
        int attempt,
        int? statusCode,
        string error,
        CancellationToken cancellationToken)
    {
        var deadLetter = attempt >= _options.MesMaximumAttempts;
        var retrySeconds = Math.Min(
            _options.MesMaximumRetrySeconds,
            _options.MesInitialRetrySeconds * Math.Pow(2, Math.Min(20, Math.Max(0, attempt - 1))));
        var nextAttempt = DateTime.UtcNow.AddSeconds(retrySeconds);
        await _journal.MarkDeliveryFailedAsync(
            transition.MessageId,
            attempt,
            nextAttempt,
            deadLetter,
            statusCode,
            error,
            cancellationToken);

        _logger.LogWarning(
            "Asset health MES delivery failed. MessageId={MessageId}, EventId={EventId}, Attempt={Attempt}, DeadLetter={DeadLetter}, Error={Error}",
            transition.MessageId,
            transition.Event.EventId,
            attempt,
            deadLetter,
            error);
    }

    private bool TryBuildEndpoint(out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(_options.MesBaseUrl, UriKind.Absolute, out var baseUri)) return false;
        if (baseUri.Scheme is not ("http" or "https")) return false;
        endpoint = new Uri(baseUri, _options.MesEndpointPath.Trim());
        return true;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private sealed record AssetHealthMesPayload
    {
        public required string MessageId { get; init; }
        public required string EventId { get; init; }
        public required int EventVersion { get; init; }
        public required AssetHealthEventTransitionType Transition { get; init; }
        public required string EventKey { get; init; }
        public required string AssetId { get; init; }
        public required AssetHealthEventLifecycleStatus LifecycleStatus { get; init; }
        public required AssetHealthGrade Grade { get; init; }
        public required AssetHealthGrade PeakGrade { get; init; }
        public required double HealthScore { get; init; }
        public required double LowestHealthScore { get; init; }
        public required DateTime FirstDetectedUtc { get; init; }
        public required DateTime LastObservedUtc { get; init; }
        public DateTime? RecoveredAtUtc { get; init; }
        public required bool Acknowledged { get; init; }
        public DateTime? AcknowledgedAtUtc { get; init; }
        public string? AcknowledgedBy { get; init; }
        public required bool IsSuppressed { get; init; }
        public DateTime? SuppressedUntilUtc { get; init; }
        public string? SuppressedReason { get; init; }
        public required string Reason { get; init; }
        public required string Source { get; init; }
        public required string Category { get; init; }
        public required DateTime OccurredAtUtc { get; init; }
        public string? Actor { get; init; }
        public string? Note { get; init; }
        public required string SourceSystem { get; init; }
    }
}
