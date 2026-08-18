namespace Wcs.Infrastructure.IndustrialIntelligence;

using System.Data;
using Microsoft.Data.SqlClient;
using Wcs.MaintenanceLearning;

public sealed class SqlMaintenanceLearningStore : IMaintenanceLearningStore, IMaintenanceLearningRecovery
{
    private readonly string _connectionString;

    public SqlMaintenanceLearningStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task SaveInterventionAsync(MaintenanceIntervention intervention, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intervention);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                IF EXISTS (SELECT 1 FROM Wcs_MaintenanceIntervention WITH (UPDLOCK,HOLDLOCK) WHERE InterventionId=@id)
                    RETURN;
                INSERT INTO Wcs_MaintenanceIntervention
                    (InterventionId,AssetId,AssetType,StartedAtUtc,CompletedAtUtc,PreFeatureSnapshotId,PostFeatureSnapshotId,ActionType,Cost,Actor,CorrelationId)
                VALUES
                    (@id,@assetId,@assetType,@started,@completed,@pre,@post,@action,@cost,@actor,@correlationId);
                """;
            command.Parameters.AddWithValue("@id", intervention.InterventionId);
            command.Parameters.AddWithValue("@assetId", intervention.AssetId);
            command.Parameters.AddWithValue("@assetType", intervention.AssetType);
            command.Parameters.AddWithValue("@started", intervention.StartedAt.UtcDateTime);
            command.Parameters.AddWithValue("@completed", intervention.CompletedAt.UtcDateTime);
            command.Parameters.AddWithValue("@pre", intervention.PreFeatureSnapshotId);
            command.Parameters.AddWithValue("@post", (object?)intervention.PostFeatureSnapshotId ?? DBNull.Value);
            command.Parameters.AddWithValue("@action", intervention.ActionType);
            command.Parameters.AddWithValue("@cost", intervention.Cost);
            command.Parameters.AddWithValue("@actor", intervention.Actor);
            command.Parameters.AddWithValue("@correlationId", intervention.CorrelationId);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task SaveOutcomeAsync(MaintenanceOutcome outcome, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                IF EXISTS (SELECT 1 FROM Wcs_MaintenanceLearningOutcome WITH (UPDLOCK,HOLDLOCK) WHERE SourceEventId=@sourceEventId)
                    RETURN;
                IF NOT EXISTS (SELECT 1 FROM Wcs_MaintenanceIntervention WHERE InterventionId=@interventionId)
                    THROW 51000, 'Intervention not found.', 1;
                INSERT INTO Wcs_MaintenanceLearningOutcome
                    (OutcomeId,InterventionId,ObservedAtUtc,FailureObserved,DowntimeMinutes,ActualCost,FailureCode,SourceEventId)
                VALUES
                    (@outcomeId,@interventionId,@observedAt,@failureObserved,@downtime,@actualCost,@failureCode,@sourceEventId);
                """;
            command.Parameters.AddWithValue("@outcomeId", outcome.OutcomeId);
            command.Parameters.AddWithValue("@interventionId", outcome.InterventionId);
            command.Parameters.AddWithValue("@observedAt", outcome.ObservedAt.UtcDateTime);
            command.Parameters.AddWithValue("@failureObserved", outcome.FailureObserved);
            command.Parameters.AddWithValue("@downtime", outcome.DowntimeMinutes);
            command.Parameters.AddWithValue("@actualCost", outcome.ActualCost);
            command.Parameters.AddWithValue("@failureCode", (object?)outcome.FailureCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@sourceEventId", outcome.SourceEventId);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task SaveEvaluationAsync(MaintenanceEvaluationResult evaluation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE Wcs_MaintenanceEffectiveness AS target
            USING (SELECT @interventionId AS InterventionId, @windowVersion AS EvaluationWindowVersion) AS source
              ON target.InterventionId=source.InterventionId AND target.EvaluationWindowVersion=source.EvaluationWindowVersion
            WHEN MATCHED THEN UPDATE SET Status=@status,EvaluatedAtUtc=@evaluatedAt,DowntimeDeltaMinutes=@downtimeDelta,
                CostDelta=@costDelta,FailureAvoided=@failureAvoided,Reason=@reason,EvidenceHash=@evidenceHash
            WHEN NOT MATCHED THEN INSERT
                (InterventionId,EvaluationWindowVersion,Status,EvaluatedAtUtc,DowntimeDeltaMinutes,CostDelta,FailureAvoided,Reason,EvidenceHash)
                VALUES(@interventionId,@windowVersion,@status,@evaluatedAt,@downtimeDelta,@costDelta,@failureAvoided,@reason,@evidenceHash);
            """;
        command.Parameters.AddWithValue("@interventionId", evaluation.InterventionId);
        command.Parameters.AddWithValue("@windowVersion", evaluation.WindowVersion);
        command.Parameters.AddWithValue("@status", evaluation.Status.ToString());
        command.Parameters.AddWithValue("@evaluatedAt", evaluation.EvaluatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@downtimeDelta", (object?)evaluation.Effectiveness?.DowntimeDeltaMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("@costDelta", (object?)evaluation.Effectiveness?.CostDelta ?? DBNull.Value);
        command.Parameters.AddWithValue("@failureAvoided", (object?)evaluation.Effectiveness?.FailureAvoided ?? DBNull.Value);
        command.Parameters.AddWithValue("@reason", evaluation.Reason);
        command.Parameters.AddWithValue("@evidenceHash", evaluation.EvidenceHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveLabelAsync(TrainingLabelCandidate label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM Wcs_MaintenanceTrainingLabel WHERE LabelId=@labelId)
            INSERT INTO Wcs_MaintenanceTrainingLabel
                (LabelId,InterventionId,DatasetKey,Label,State,EvidenceHash,CreatedAtUtc)
            VALUES(@labelId,@interventionId,@datasetKey,@label,@state,@evidenceHash,@createdAtUtc);
            """;
        command.Parameters.AddWithValue("@labelId", label.LabelId);
        command.Parameters.AddWithValue("@interventionId", label.InterventionId);
        command.Parameters.AddWithValue("@datasetKey", label.DatasetKey);
        command.Parameters.AddWithValue("@label", label.Label);
        command.Parameters.AddWithValue("@state", label.State.ToString());
        command.Parameters.AddWithValue("@evidenceHash", label.EvidenceHash);
        command.Parameters.AddWithValue("@createdAtUtc", label.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveApprovalAsync(TrainingLabelApproval approval, string correlationId, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        Require(correlationId, nameof(correlationId));
        Require(idempotencyKey, nameof(idempotencyKey));
        if (approval.State == TrainingLabelApprovalState.Pending)
            throw new InvalidOperationException("Approval decision cannot remain Pending.");

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                IF EXISTS (SELECT 1 FROM Wcs_MaintenanceTrainingLabelApproval WITH (UPDLOCK,HOLDLOCK) WHERE IdempotencyKey=@idempotencyKey)
                    RETURN;
                UPDATE Wcs_MaintenanceTrainingLabel
                   SET State=@state
                 WHERE LabelId=@labelId AND State='Pending';
                IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM Wcs_MaintenanceTrainingLabel WHERE LabelId=@labelId AND State=@state)
                    THROW 51001, 'Training label is missing or already decided differently.', 1;
                INSERT INTO Wcs_MaintenanceTrainingLabelApproval
                    (LabelId,State,Actor,Reason,DecidedAtUtc,CorrelationId,IdempotencyKey)
                VALUES(@labelId,@state,@actor,@reason,@decidedAtUtc,@correlationId,@idempotencyKey);
                """;
            command.Parameters.AddWithValue("@labelId", approval.LabelId);
            command.Parameters.AddWithValue("@state", approval.State.ToString());
            command.Parameters.AddWithValue("@actor", approval.Actor);
            command.Parameters.AddWithValue("@reason", approval.Reason);
            command.Parameters.AddWithValue("@decidedAtUtc", approval.DecidedAt.UtcDateTime);
            command.Parameters.AddWithValue("@correlationId", correlationId);
            command.Parameters.AddWithValue("@idempotencyKey", idempotencyKey);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task SaveOutboxAsync(MesOutboxEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE Wcs_MaintenanceMesOutbox AS target
            USING (SELECT @idempotencyKey AS IdempotencyKey) AS source ON target.IdempotencyKey=source.IdempotencyKey
            WHEN MATCHED THEN UPDATE SET AttemptCount=@attemptCount,LastAttemptAtUtc=@lastAttemptAt,DeliveredAtUtc=@deliveredAt,LastError=@lastError
            WHEN NOT MATCHED THEN INSERT
                (OutboxId,InterventionId,IdempotencyKey,PayloadHash,AttemptCount,CreatedAtUtc,LastAttemptAtUtc,DeliveredAtUtc,LastError)
                VALUES(@outboxId,@interventionId,@idempotencyKey,@payloadHash,@attemptCount,@createdAt,@lastAttemptAt,@deliveredAt,@lastError);
            """;
        command.Parameters.AddWithValue("@outboxId", entry.OutboxId);
        command.Parameters.AddWithValue("@interventionId", entry.InterventionId);
        command.Parameters.AddWithValue("@idempotencyKey", entry.IdempotencyKey);
        command.Parameters.AddWithValue("@payloadHash", entry.PayloadHash);
        command.Parameters.AddWithValue("@attemptCount", entry.AttemptCount);
        command.Parameters.AddWithValue("@createdAt", entry.CreatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@lastAttemptAt", (object?)entry.LastAttemptAtUtc?.UtcDateTime ?? DBNull.Value);
        command.Parameters.AddWithValue("@deliveredAt", (object?)entry.DeliveredAtUtc?.UtcDateTime ?? DBNull.Value);
        command.Parameters.AddWithValue("@lastError", (object?)entry.LastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<MaintenanceIntervention?> GetInterventionAsync(string interventionId, CancellationToken ct = default)
    {
        Require(interventionId, nameof(interventionId));
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT InterventionId,AssetId,AssetType,StartedAtUtc,CompletedAtUtc,PreFeatureSnapshotId,PostFeatureSnapshotId,ActionType,Cost,Actor,CorrelationId
            FROM Wcs_MaintenanceIntervention WHERE InterventionId=@id;
            """;
        command.Parameters.AddWithValue("@id", interventionId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new MaintenanceIntervention(reader.GetString(0),reader.GetString(1),reader.GetString(2),Utc(reader.GetDateTime(3)),Utc(reader.GetDateTime(4)),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),reader.GetString(7),reader.GetDecimal(8),reader.GetString(9),reader.GetString(10));
    }

    public async Task<IReadOnlyList<MesOutboxEntry>> LoadPendingOutboxAsync(int take, CancellationToken ct = default)
    {
        if (take is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(take));
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@take) OutboxId,InterventionId,IdempotencyKey,PayloadHash,AttemptCount,CreatedAtUtc,LastAttemptAtUtc,DeliveredAtUtc,LastError
            FROM Wcs_MaintenanceMesOutbox WHERE DeliveredAtUtc IS NULL ORDER BY CreatedAtUtc,Id;
            """;
        command.Parameters.AddWithValue("@take", take);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<MesOutboxEntry>();
        while (await reader.ReadAsync(ct))
            result.Add(new MesOutboxEntry(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4),Utc(reader.GetDateTime(5)),reader.IsDBNull(6)?null:Utc(reader.GetDateTime(6)),reader.IsDBNull(7)?null:Utc(reader.GetDateTime(7)),reader.IsDBNull(8)?null:reader.GetString(8)));
        return result;
    }

    public async Task<MaintenanceLearningRecoverySnapshot> RecoverAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM Wcs_MaintenanceIntervention),
              (SELECT COUNT(*) FROM Wcs_MaintenanceMesOutbox WHERE DeliveredAtUtc IS NULL),
              (SELECT COUNT(*) FROM Wcs_MaintenanceTrainingLabel WHERE State='Pending');
            """;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        await reader.ReadAsync(ct);
        var interventions = reader.GetInt32(0);
        var pendingOutbox = reader.GetInt32(1);
        var pendingLabels = reader.GetInt32(2);
        var stateHash = MaintenanceLearningHash.Sha256(interventions.ToString(), pendingOutbox.ToString(), pendingLabels.ToString());
        return new MaintenanceLearningRecoverySnapshot(interventions,pendingOutbox,pendingLabels,stateHash);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }
}
