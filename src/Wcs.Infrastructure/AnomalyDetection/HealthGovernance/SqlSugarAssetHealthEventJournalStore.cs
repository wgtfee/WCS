namespace Wcs.Infrastructure.AnomalyDetection.HealthGovernance;

using Microsoft.Extensions.Logging;
using SqlSugar;
using Wcs.Core.AnomalyDetection.HealthGovernance;
using Wcs.Core.AnomalyDetection.HealthScoring;

[SugarTable("Wcs_AssetHealthEventJournal")]
public sealed class AssetHealthEventJournalEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Sequence { get; set; }

    [SugarColumn(Length = 64)]
    public string MessageId { get; set; } = string.Empty;

    [SugarColumn(Length = 32)]
    public string EventId { get; set; } = string.Empty;

    [SugarColumn(Length = 160)]
    public string EventKey { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string AssetId { get; set; } = string.Empty;

    public int EventVersion { get; set; }
    public int TransitionType { get; set; }
    public int LifecycleStatus { get; set; }
    public int Grade { get; set; }
    public int PeakGrade { get; set; }
    public double HealthScore { get; set; }
    public double LowestHealthScore { get; set; }
    public DateTime FirstDetectedUtc { get; set; }
    public DateTime LastObservedUtc { get; set; }
    public DateTime? RecoveredAtUtc { get; set; }
    public bool Acknowledged { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }

    [SugarColumn(Length = 128)]
    public string? AcknowledgedBy { get; set; }

    public bool IsSuppressed { get; set; }
    public DateTime? SuppressedUntilUtc { get; set; }

    [SugarColumn(Length = 1000)]
    public string? SuppressedReason { get; set; }

    [SugarColumn(Length = 2000)]
    public string Reason { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Source { get; set; } = string.Empty;

    [SugarColumn(Length = 128)]
    public string Category { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    [SugarColumn(Length = 128)]
    public string? Actor { get; set; }

    [SugarColumn(Length = 2000)]
    public string? Note { get; set; }

    public int DeliveryStatus { get; set; }
    public int DeliveryAttemptCount { get; set; }
    public DateTime? NextDeliveryAttemptUtc { get; set; }
    public DateTime? LastDeliveryAttemptUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public int? LastHttpStatusCode { get; set; }

    [SugarColumn(Length = 2000)]
    public string? LastDeliveryError { get; set; }
}

/// <summary>
/// 健康事件不可变 Journal 与 MES Outbox。每次操作创建独立 SqlSugarClient，
/// 仅由诊断后台服务和治理 API 使用，不进入 PLC、任务或调度控制闭环。
/// </summary>
public sealed class SqlSugarAssetHealthEventJournalStore : IAssetHealthEventJournalStore
{
    private readonly string _connectionString;
    private readonly AssetHealthGovernanceOptions _options;
    private readonly ILogger<SqlSugarAssetHealthEventJournalStore> _logger;
    private readonly object _schemaGate = new();
    private readonly object _statusGate = new();

    private bool _schemaReady;
    private DateTime? _lastSuccessfulWriteUtc;
    private DateTime? _lastSuccessfulDeliveryUtc;
    private DateTime _nextMaintenanceUtc = DateTime.MinValue;
    private string? _lastError;

    public SqlSugarAssetHealthEventJournalStore(
        string connectionString,
        AssetHealthGovernanceOptions options,
        ILogger<SqlSugarAssetHealthEventJournalStore> logger)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("WcsDb connection string is required.", nameof(connectionString))
            : connectionString;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Provider => "SqlServer";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> AppendAsync(
        AssetHealthEventTransition transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSchema();

        try
        {
            using var db = CreateClient();
            if (db.Queryable<AssetHealthEventJournalEntity>()
                .Any(row => row.MessageId == transition.MessageId))
                return ValueTask.FromResult(false);

            db.Insertable(ToEntity(transition)).ExecuteCommand();
            lock (_statusGate)
            {
                _lastSuccessfulWriteUtc = DateTime.UtcNow;
                _lastError = null;
            }
            return ValueTask.FromResult(true);
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
            _logger.LogWarning(
                exception,
                "Asset health event transition persistence failed. MessageId={MessageId}, EventId={EventId}",
                transition.MessageId,
                transition.Event.EventId);
            throw;
        }
    }

    public ValueTask<IReadOnlyList<AssetHealthEventTransition>> LoadLatestAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumTrackedAssets);
        EnsureSchema();

        using var db = CreateClient();
        var scanCount = Math.Min(200_000, Math.Max(maximumCount, maximumCount * 20));
        var latest = db.Queryable<AssetHealthEventJournalEntity>()
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(scanCount)
            .ToList()
            .GroupBy(static row => row.EventId, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static row => row.EventVersion)
                .ThenByDescending(static row => row.Sequence)
                .First())
            .OrderByDescending(static row => row.LastObservedUtc)
            .Take(maximumCount)
            .Select(ToTransition)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AssetHealthEventTransition>>(latest);
    }

    public ValueTask<IReadOnlyList<AssetHealthEventTransition>> GetHistoryAsync(
        string eventId,
        int maximumCount = 200,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(eventId))
            return ValueTask.FromResult<IReadOnlyList<AssetHealthEventTransition>>(
                Array.Empty<AssetHealthEventTransition>());

        maximumCount = Math.Clamp(maximumCount, 1, _options.MaximumEventsQueryCount);
        EnsureSchema();
        using var db = CreateClient();
        var items = db.Queryable<AssetHealthEventJournalEntity>()
            .Where(row => row.EventId == eventId.Trim())
            .OrderBy(row => row.Sequence, OrderByType.Desc)
            .Take(maximumCount)
            .ToList()
            .Select(ToTransition)
            .Reverse()
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AssetHealthEventTransition>>(items);
    }

    public ValueTask<IReadOnlyList<AssetHealthEventTransition>> GetPendingDeliveriesAsync(
        DateTime utcNow,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        maximumCount = Math.Clamp(maximumCount, 1, _options.MesBatchSize);
        EnsureSchema();
        using var db = CreateClient();
        var pending = (int)AssetHealthDeliveryStatus.Pending;
        var retrying = (int)AssetHealthDeliveryStatus.Retrying;
        var items = db.Queryable<AssetHealthEventJournalEntity>()
            .Where(row =>
                (row.DeliveryStatus == pending || row.DeliveryStatus == retrying) &&
                (row.NextDeliveryAttemptUtc == null || row.NextDeliveryAttemptUtc <= utcNow))
            .OrderBy(row => row.Sequence, OrderByType.Asc)
            .Take(maximumCount)
            .ToList()
            .Select(ToTransition)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AssetHealthEventTransition>>(items);
    }

    public ValueTask MarkDeliveredAsync(
        string messageId,
        DateTime deliveredAtUtc,
        int? httpStatusCode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(messageId)) return ValueTask.CompletedTask;
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthEventJournalEntity>()
            .Where(row => row.MessageId == messageId.Trim())
            .First();
        if (entity is null) return ValueTask.CompletedTask;

        entity.DeliveryStatus = (int)AssetHealthDeliveryStatus.Delivered;
        entity.DeliveredAtUtc = deliveredAtUtc;
        entity.LastDeliveryAttemptUtc = deliveredAtUtc;
        entity.NextDeliveryAttemptUtc = null;
        entity.LastHttpStatusCode = httpStatusCode;
        entity.LastDeliveryError = null;
        db.Updateable(entity).ExecuteCommand();
        lock (_statusGate)
        {
            _lastSuccessfulDeliveryUtc = deliveredAtUtc;
            _lastError = null;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDeliveryFailedAsync(
        string messageId,
        int attemptCount,
        DateTime nextAttemptUtc,
        bool deadLetter,
        int? httpStatusCode,
        string error,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(messageId)) return ValueTask.CompletedTask;
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthEventJournalEntity>()
            .Where(row => row.MessageId == messageId.Trim())
            .First();
        if (entity is null) return ValueTask.CompletedTask;

        entity.DeliveryStatus = (int)(deadLetter
            ? AssetHealthDeliveryStatus.DeadLetter
            : AssetHealthDeliveryStatus.Retrying);
        entity.DeliveryAttemptCount = attemptCount;
        entity.LastDeliveryAttemptUtc = DateTime.UtcNow;
        entity.NextDeliveryAttemptUtc = deadLetter ? null : nextAttemptUtc;
        entity.LastHttpStatusCode = httpStatusCode;
        entity.LastDeliveryError = Truncate(error, 2000);
        db.Updateable(entity).ExecuteCommand();
        SetLastError(entity.LastDeliveryError ?? "MES delivery failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RetryDeliveryAsync(
        string messageId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(messageId)) return ValueTask.FromResult(false);
        EnsureSchema();
        using var db = CreateClient();
        var entity = db.Queryable<AssetHealthEventJournalEntity>()
            .Where(row => row.MessageId == messageId.Trim())
            .First();
        if (entity is null || entity.DeliveryStatus == (int)AssetHealthDeliveryStatus.Delivered)
            return ValueTask.FromResult(false);

        entity.DeliveryStatus = (int)AssetHealthDeliveryStatus.Pending;
        entity.DeliveryAttemptCount = 0;
        entity.NextDeliveryAttemptUtc = utcNow;
        entity.LastDeliveryAttemptUtc = null;
        entity.DeliveredAtUtc = null;
        entity.LastHttpStatusCode = null;
        entity.LastDeliveryError = null;
        db.Updateable(entity).ExecuteCommand();
        return ValueTask.FromResult(true);
    }

    public ValueTask<AssetHealthEventJournalStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = false;
        var retainedTransitions = 0;
        var retainedEvents = 0;
        var pendingDeliveries = 0;
        var retryingDeliveries = 0;
        var deliveredMessages = 0;
        var deadLetterMessages = 0;

        try
        {
            EnsureSchema();
            using var db = CreateClient();
            retainedTransitions = db.Queryable<AssetHealthEventJournalEntity>().Count();
            retainedEvents = db.Queryable<AssetHealthEventJournalEntity>()
                .Select(row => row.EventId)
                .Distinct()
                .ToList()
                .Count;
            pendingDeliveries = db.Queryable<AssetHealthEventJournalEntity>()
                .Count(row => row.DeliveryStatus == (int)AssetHealthDeliveryStatus.Pending);
            retryingDeliveries = db.Queryable<AssetHealthEventJournalEntity>()
                .Count(row => row.DeliveryStatus == (int)AssetHealthDeliveryStatus.Retrying);
            deliveredMessages = db.Queryable<AssetHealthEventJournalEntity>()
                .Count(row => row.DeliveryStatus == (int)AssetHealthDeliveryStatus.Delivered);
            deadLetterMessages = db.Queryable<AssetHealthEventJournalEntity>()
                .Count(row => row.DeliveryStatus == (int)AssetHealthDeliveryStatus.DeadLetter);
            available = true;
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
        }

        DateTime? lastWrite;
        DateTime? lastDelivery;
        string? lastError;
        lock (_statusGate)
        {
            lastWrite = _lastSuccessfulWriteUtc;
            lastDelivery = _lastSuccessfulDeliveryUtc;
            lastError = _lastError;
        }

        return ValueTask.FromResult(new AssetHealthEventJournalStatus
        {
            Enabled = _options.Enabled,
            Provider = Provider,
            IsAvailable = available,
            RetainedTransitions = retainedTransitions,
            RetainedEvents = retainedEvents,
            PendingDeliveries = pendingDeliveries,
            RetryingDeliveries = retryingDeliveries,
            DeliveredMessages = deliveredMessages,
            DeadLetterMessages = deadLetterMessages,
            LastSuccessfulWriteUtc = lastWrite,
            LastSuccessfulDeliveryUtc = lastDelivery,
            LastError = lastError
        });
    }

    public ValueTask MaintainAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled || utcNow < _nextMaintenanceUtc)
            return ValueTask.CompletedTask;

        _nextMaintenanceUtc = utcNow.AddSeconds(_options.MaintenanceIntervalSeconds);
        try
        {
            EnsureSchema();
            using var db = CreateClient();
            var parameters = new[]
            {
                new SugarParameter("@BatchSize", _options.MaintenanceBatchSize),
                new SugarParameter("@CutoffUtc", utcNow.AddHours(-_options.EventRetentionHours)),
                new SugarParameter("@Pending", (int)AssetHealthDeliveryStatus.Pending),
                new SugarParameter("@Retrying", (int)AssetHealthDeliveryStatus.Retrying)
            };
            db.Ado.ExecuteCommand(@"
DELETE TOP (@BatchSize)
FROM Wcs_AssetHealthEventJournal
WHERE OccurredAtUtc < @CutoffUtc
  AND DeliveryStatus <> @Pending
  AND DeliveryStatus <> @Retrying;", parameters);
        }
        catch (Exception exception)
        {
            SetLastError(exception.Message);
            _nextMaintenanceUtc = utcNow.AddSeconds(Math.Min(60, _options.MaintenanceIntervalSeconds));
            _logger.LogWarning(exception, "Asset health event journal maintenance failed.");
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_schemaGate)
        {
            if (_schemaReady) return;
            using var db = CreateClient();
            db.CodeFirst.InitTables<AssetHealthEventJournalEntity>();
            db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AssetHealthEventJournal_MessageId' AND object_id = OBJECT_ID('Wcs_AssetHealthEventJournal'))
    CREATE UNIQUE INDEX UX_Wcs_AssetHealthEventJournal_MessageId ON Wcs_AssetHealthEventJournal(MessageId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_AssetHealthEventJournal_EventVersion' AND object_id = OBJECT_ID('Wcs_AssetHealthEventJournal'))
    CREATE UNIQUE INDEX UX_Wcs_AssetHealthEventJournal_EventVersion ON Wcs_AssetHealthEventJournal(EventId, EventVersion);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AssetHealthEventJournal_AssetTime' AND object_id = OBJECT_ID('Wcs_AssetHealthEventJournal'))
    CREATE INDEX IX_Wcs_AssetHealthEventJournal_AssetTime ON Wcs_AssetHealthEventJournal(AssetId, OccurredAtUtc DESC, Sequence DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_AssetHealthEventJournal_Delivery' AND object_id = OBJECT_ID('Wcs_AssetHealthEventJournal'))
    CREATE INDEX IX_Wcs_AssetHealthEventJournal_Delivery ON Wcs_AssetHealthEventJournal(DeliveryStatus, NextDeliveryAttemptUtc, Sequence);" );
            _schemaReady = true;
        }
    }

    private SqlSugarClient CreateClient() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });

    private static AssetHealthEventJournalEntity ToEntity(AssetHealthEventTransition transition)
    {
        var snapshot = transition.Event;
        return new AssetHealthEventJournalEntity
        {
            MessageId = transition.MessageId,
            EventId = snapshot.EventId,
            EventKey = snapshot.EventKey,
            AssetId = snapshot.AssetId,
            EventVersion = snapshot.Version,
            TransitionType = (int)transition.TransitionType,
            LifecycleStatus = (int)snapshot.LifecycleStatus,
            Grade = (int)snapshot.Grade,
            PeakGrade = (int)snapshot.PeakGrade,
            HealthScore = snapshot.HealthScore,
            LowestHealthScore = snapshot.LowestHealthScore,
            FirstDetectedUtc = snapshot.FirstDetectedUtc,
            LastObservedUtc = snapshot.LastObservedUtc,
            RecoveredAtUtc = snapshot.RecoveredAtUtc,
            Acknowledged = snapshot.Acknowledged,
            AcknowledgedAtUtc = snapshot.AcknowledgedAtUtc,
            AcknowledgedBy = snapshot.AcknowledgedBy,
            IsSuppressed = snapshot.IsSuppressed,
            SuppressedUntilUtc = snapshot.SuppressedUntilUtc,
            SuppressedReason = snapshot.SuppressedReason,
            Reason = Truncate(snapshot.Reason, 2000),
            Source = Truncate(snapshot.Source, 128),
            Category = Truncate(snapshot.Category, 128),
            OccurredAtUtc = transition.OccurredAtUtc,
            Actor = Truncate(transition.Actor, 128),
            Note = Truncate(transition.Note, 2000),
            DeliveryStatus = (int)transition.DeliveryStatus,
            DeliveryAttemptCount = transition.DeliveryAttemptCount,
            NextDeliveryAttemptUtc = transition.NextDeliveryAttemptUtc,
            LastDeliveryAttemptUtc = transition.LastDeliveryAttemptUtc,
            DeliveredAtUtc = transition.DeliveredAtUtc,
            LastHttpStatusCode = transition.LastHttpStatusCode,
            LastDeliveryError = Truncate(transition.LastDeliveryError, 2000)
        };
    }

    private static AssetHealthEventTransition ToTransition(AssetHealthEventJournalEntity entity)
    {
        var snapshot = new AssetHealthEventSnapshot
        {
            EventId = entity.EventId,
            EventKey = entity.EventKey,
            AssetId = entity.AssetId,
            Version = entity.EventVersion,
            LifecycleStatus = (AssetHealthEventLifecycleStatus)entity.LifecycleStatus,
            Grade = (AssetHealthGrade)entity.Grade,
            PeakGrade = (AssetHealthGrade)entity.PeakGrade,
            HealthScore = entity.HealthScore,
            LowestHealthScore = entity.LowestHealthScore,
            FirstDetectedUtc = entity.FirstDetectedUtc,
            LastObservedUtc = entity.LastObservedUtc,
            RecoveredAtUtc = entity.RecoveredAtUtc,
            Acknowledged = entity.Acknowledged,
            AcknowledgedAtUtc = entity.AcknowledgedAtUtc,
            AcknowledgedBy = entity.AcknowledgedBy,
            IsSuppressed = entity.IsSuppressed,
            SuppressedUntilUtc = entity.SuppressedUntilUtc,
            SuppressedReason = entity.SuppressedReason,
            Reason = entity.Reason,
            Source = entity.Source,
            Category = entity.Category
        };
        return new AssetHealthEventTransition
        {
            Sequence = entity.Sequence,
            MessageId = entity.MessageId,
            TransitionType = (AssetHealthEventTransitionType)entity.TransitionType,
            Event = snapshot,
            OccurredAtUtc = entity.OccurredAtUtc,
            Actor = entity.Actor,
            Note = entity.Note,
            DeliveryStatus = (AssetHealthDeliveryStatus)entity.DeliveryStatus,
            DeliveryAttemptCount = entity.DeliveryAttemptCount,
            NextDeliveryAttemptUtc = entity.NextDeliveryAttemptUtc,
            LastDeliveryAttemptUtc = entity.LastDeliveryAttemptUtc,
            DeliveredAtUtc = entity.DeliveredAtUtc,
            LastHttpStatusCode = entity.LastHttpStatusCode,
            LastDeliveryError = entity.LastDeliveryError
        };
    }

    private void SetLastError(string error)
    {
        lock (_statusGate) _lastError = Truncate(error, 2000);
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= maximumLength ? value : value[..maximumLength];
}
