namespace Wcs.Infrastructure.IndustrialIntelligence;

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Wcs.ModelOps;

public sealed class SqlModelRegistry : IModelRegistry
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public SqlModelRegistry(string connectionString)
    {
        _connectionString = RequireConnectionString(connectionString);
    }

    public async Task RegisterAsync(AiModelVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        var errors = ModelOpsContractRules.ValidateManifest(version.Manifest);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(version));
        if (string.IsNullOrWhiteSpace(version.RegisteredBy) || string.IsNullOrWhiteSpace(version.CorrelationId))
            throw new ArgumentException("RegisteredBy and CorrelationId are required.", nameof(version));

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT ManifestHash
                FROM Wcs_AiModelRegistry WITH (UPDLOCK, HOLDLOCK)
                WHERE ModelId = @modelId AND ModelVersion = @modelVersion;
                """;
            select.Parameters.AddWithValue("@modelId", version.ModelId.Trim());
            select.Parameters.AddWithValue("@modelVersion", version.Version.Trim());
            var existing = await select.ExecuteScalarAsync(ct);
            if (existing is not null && existing is not DBNull)
            {
                var existingHash = Convert.ToString(existing) ?? string.Empty;
                if (!string.Equals(existingHash, version.Manifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Model '{version.ModelId}' version '{version.Version}' already exists with a different ManifestHash.");
                await transaction.CommitAsync(ct);
                return;
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Wcs_AiModelRegistry
                    (ModelId, ModelVersion, ModelType, ManifestHash, ManifestJson, LifecycleStatus,
                     CreatedAtUtc, CreatedBy, CorrelationId)
                VALUES
                    (@modelId, @modelVersion, @modelType, @manifestHash, @manifestJson, @lifecycleStatus,
                     @createdAtUtc, @createdBy, @correlationId);
                """;
            insert.Parameters.AddWithValue("@modelId", version.ModelId.Trim());
            insert.Parameters.AddWithValue("@modelVersion", version.Version.Trim());
            insert.Parameters.AddWithValue("@modelType", version.Manifest.ModelType.Trim());
            insert.Parameters.AddWithValue("@manifestHash", version.Manifest.ManifestHash.ToLowerInvariant());
            insert.Parameters.AddWithValue("@manifestJson", JsonSerializer.Serialize(version.Manifest, _json));
            insert.Parameters.AddWithValue("@lifecycleStatus", version.LifecycleStatus.ToString());
            insert.Parameters.AddWithValue("@createdAtUtc", version.RegisteredAtUtc.UtcDateTime);
            insert.Parameters.AddWithValue("@createdBy", version.RegisteredBy.Trim());
            insert.Parameters.AddWithValue("@correlationId", version.CorrelationId.Trim());
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<AiModelVersion?> GetAsync(string modelId, string version, CancellationToken ct)
    {
        Require(modelId, nameof(modelId));
        Require(version, nameof(version));
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ManifestJson, LifecycleStatus, CreatedAtUtc, CreatedBy, CorrelationId
            FROM Wcs_AiModelRegistry
            WHERE ModelId = @modelId AND ModelVersion = @modelVersion;
            """;
        command.Parameters.AddWithValue("@modelId", modelId.Trim());
        command.Parameters.AddWithValue("@modelVersion", version.Trim());
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadVersion(reader);
    }

    public async Task<IReadOnlyList<AiModelVersion>> ListAsync(string modelId, CancellationToken ct)
    {
        Require(modelId, nameof(modelId));
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ManifestJson, LifecycleStatus, CreatedAtUtc, CreatedBy, CorrelationId
            FROM Wcs_AiModelRegistry
            WHERE ModelId = @modelId
            ORDER BY ModelVersion;
            """;
        command.Parameters.AddWithValue("@modelId", modelId.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AiModelVersion>();
        while (await reader.ReadAsync(ct))
            result.Add(ReadVersion(reader));
        return result;
    }

    private AiModelVersion ReadVersion(SqlDataReader reader)
    {
        var manifest = JsonSerializer.Deserialize<AiModelPackageManifest>(reader.GetString(0), _json)
            ?? throw new InvalidOperationException("Wcs_AiModelRegistry contains an invalid ManifestJson payload.");
        if (!Enum.TryParse<AiModelLifecycleStatus>(reader.GetString(1), true, out var status))
            throw new InvalidOperationException("Wcs_AiModelRegistry contains an invalid LifecycleStatus.");
        return new AiModelVersion(
            manifest,
            status,
            AsUtc(reader.GetDateTime(2)),
            reader.GetString(3),
            reader.GetString(4));
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static string RequireConnectionString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Connection string is required.", nameof(value));
        return value;
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
    }

    internal static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class SqlModelDeploymentStore : IModelDeploymentStore
{
    private readonly string _connectionString;

    public SqlModelDeploymentStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task<IReadOnlyList<AiModelDeployment>> ListScopeAsync(
        string modelId,
        string assetType,
        string profile,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ModelId, ModelVersion, AssetType, Profile, DeploymentStatus,
                   UpdatedAtUtc, Actor, Reason, CorrelationId
            FROM Wcs_AiModelDeployment
            WHERE ModelId = @modelId AND AssetType = @assetType AND Profile = @profile
            ORDER BY ModelVersion;
            """;
        command.Parameters.AddWithValue("@modelId", modelId.Trim());
        command.Parameters.AddWithValue("@assetType", assetType.Trim());
        command.Parameters.AddWithValue("@profile", profile.Trim());
        return await ReadDeploymentsAsync(command, ct);
    }

    public async Task<IReadOnlyList<AiModelDeployment>> ListAllAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ModelId, ModelVersion, AssetType, Profile, DeploymentStatus,
                   UpdatedAtUtc, Actor, Reason, CorrelationId
            FROM Wcs_AiModelDeployment
            ORDER BY ModelId, AssetType, Profile, ModelVersion;
            """;
        return await ReadDeploymentsAsync(command, ct);
    }

    public async Task ApplyAsync(IReadOnlyList<AiModelDeployment> deployments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        if (deployments.Count == 0)
            return;
        foreach (var deployment in deployments)
            Validate(deployment);

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var replacementScopes = deployments
                .Where(x => x.Status is AiModelLifecycleStatus.Champion or AiModelLifecycleStatus.Fallback)
                .Select(x => (x.ModelId, x.AssetType, x.Profile))
                .Distinct()
                .ToArray();

            foreach (var scope in replacementScopes)
            {
                await using var reset = connection.CreateCommand();
                reset.Transaction = transaction;
                reset.CommandText = """
                    UPDATE Wcs_AiModelDeployment
                    SET DeploymentStatus = 'Retired'
                    WHERE ModelId = @modelId AND AssetType = @assetType AND Profile = @profile
                      AND DeploymentStatus IN ('Champion','Fallback');
                    """;
                reset.Parameters.AddWithValue("@modelId", scope.ModelId);
                reset.Parameters.AddWithValue("@assetType", scope.AssetType);
                reset.Parameters.AddWithValue("@profile", scope.Profile);
                await reset.ExecuteNonQueryAsync(ct);
            }

            foreach (var deployment in deployments)
                await UpsertAsync(connection, transaction, deployment, ct);

            foreach (var scope in deployments
                         .Select(x => (x.ModelId, x.AssetType, x.Profile))
                         .Distinct())
            {
                await using var verify = connection.CreateCommand();
                verify.Transaction = transaction;
                verify.CommandText = """
                    SELECT
                      SUM(CASE WHEN DeploymentStatus='Champion' THEN 1 ELSE 0 END),
                      SUM(CASE WHEN DeploymentStatus='Fallback' THEN 1 ELSE 0 END)
                    FROM Wcs_AiModelDeployment WITH (UPDLOCK, HOLDLOCK)
                    WHERE ModelId=@modelId AND AssetType=@assetType AND Profile=@profile;
                    """;
                verify.Parameters.AddWithValue("@modelId", scope.ModelId);
                verify.Parameters.AddWithValue("@assetType", scope.AssetType);
                verify.Parameters.AddWithValue("@profile", scope.Profile);
                await using var reader = await verify.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
                await reader.ReadAsync(ct);
                var champions = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                var fallbacks = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                if (champions > 1 || fallbacks > 1)
                    throw new ModelDeploymentInvariantException(
                        $"SQL scope has invalid Champion/Fallback cardinality: champion={champions}, fallback={fallbacks}.");
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task UpsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AiModelDeployment deployment,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Wcs_AiModelDeployment
            SET DeploymentStatus=@status, UpdatedAtUtc=@updatedAtUtc, Actor=@actor,
                Reason=@reason, CorrelationId=@correlationId
            WHERE ModelId=@modelId AND ModelVersion=@modelVersion
              AND AssetType=@assetType AND Profile=@profile;
            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO Wcs_AiModelDeployment
                    (ModelId, ModelVersion, AssetType, Profile, DeploymentStatus,
                     UpdatedAtUtc, Actor, Reason, CorrelationId)
                VALUES
                    (@modelId, @modelVersion, @assetType, @profile, @status,
                     @updatedAtUtc, @actor, @reason, @correlationId);
            END
            """;
        command.Parameters.AddWithValue("@modelId", deployment.ModelId.Trim());
        command.Parameters.AddWithValue("@modelVersion", deployment.ModelVersion.Trim());
        command.Parameters.AddWithValue("@assetType", deployment.AssetType.Trim());
        command.Parameters.AddWithValue("@profile", deployment.Profile.Trim());
        command.Parameters.AddWithValue("@status", deployment.Status.ToString());
        command.Parameters.AddWithValue("@updatedAtUtc", deployment.UpdatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@actor", deployment.Actor.Trim());
        command.Parameters.AddWithValue("@reason", deployment.Reason.Trim());
        command.Parameters.AddWithValue("@correlationId", deployment.CorrelationId.Trim());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<AiModelDeployment>> ReadDeploymentsAsync(
        SqlCommand command,
        CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AiModelDeployment>();
        while (await reader.ReadAsync(ct))
        {
            if (!Enum.TryParse<AiModelLifecycleStatus>(reader.GetString(4), true, out var status))
                throw new InvalidOperationException("Wcs_AiModelDeployment contains an invalid DeploymentStatus.");
            result.Add(new AiModelDeployment(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                status,
                SqlModelRegistry.AsUtc(reader.GetDateTime(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }
        return result;
    }

    private static void Validate(AiModelDeployment deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment.ModelId) ||
            string.IsNullOrWhiteSpace(deployment.ModelVersion) ||
            string.IsNullOrWhiteSpace(deployment.AssetType) ||
            string.IsNullOrWhiteSpace(deployment.Profile) ||
            string.IsNullOrWhiteSpace(deployment.Actor) ||
            string.IsNullOrWhiteSpace(deployment.Reason) ||
            string.IsNullOrWhiteSpace(deployment.CorrelationId))
            throw new ArgumentException("Deployment fields are required.", nameof(deployment));
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

public sealed class SqlModelOpsAuditJournal : IModelOpsAuditJournal
{
    private readonly string _connectionString;

    public SqlModelOpsAuditJournal(string connectionString)
    {
        _connectionString = Require(connectionString);
    }

    public async Task AppendAsync(AiModelAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Wcs_AiModelAuditJournal
              (AuditId, Action, ModelId, ModelVersion, Actor, Reason,
               OccurredAtUtc, CorrelationId, PayloadHash)
            VALUES
              (@auditId, @action, @modelId, @modelVersion, @actor, @reason,
               @occurredAtUtc, @correlationId, @payloadHash);
            """;
        command.Parameters.AddWithValue("@auditId", entry.AuditId);
        command.Parameters.AddWithValue("@action", entry.Action);
        command.Parameters.AddWithValue("@modelId", entry.ModelId);
        command.Parameters.AddWithValue("@modelVersion", entry.ModelVersion);
        command.Parameters.AddWithValue("@actor", entry.Actor);
        command.Parameters.AddWithValue("@reason", entry.Reason);
        command.Parameters.AddWithValue("@occurredAtUtc", entry.OccurredAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@correlationId", entry.CorrelationId);
        command.Parameters.AddWithValue("@payloadHash", entry.PayloadHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AiModelAuditEntry>> ListAsync(string? modelId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(modelId)
            ? """
              SELECT TOP (@limit) AuditId, Action, ModelId, ModelVersion, Actor, Reason,
                     OccurredAtUtc, CorrelationId, PayloadHash
              FROM Wcs_AiModelAuditJournal ORDER BY OccurredAtUtc DESC, Id DESC;
              """
            : """
              SELECT TOP (@limit) AuditId, Action, ModelId, ModelVersion, Actor, Reason,
                     OccurredAtUtc, CorrelationId, PayloadHash
              FROM Wcs_AiModelAuditJournal WHERE ModelId=@modelId ORDER BY OccurredAtUtc DESC, Id DESC;
              """;
        command.Parameters.AddWithValue("@limit", limit);
        if (!string.IsNullOrWhiteSpace(modelId))
            command.Parameters.AddWithValue("@modelId", modelId.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AiModelAuditEntry>();
        while (await reader.ReadAsync(ct))
            result.Add(new AiModelAuditEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), SqlModelRegistry.AsUtc(reader.GetDateTime(6)),
                reader.GetString(7), reader.GetString(8)));
        return result;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Connection string is required.") : value;
}

public sealed class SqlModelEvaluationStore : IModelEvaluationStore
{
    private readonly string _connectionString;

    public SqlModelEvaluationStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task AppendAsync(AiModelEvaluation evaluation, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Wcs_AiModelEvaluation
              (EvaluationId, ModelId, ModelVersion, DatasetVersion, DatasetHash,
               MetricsJson, EvidenceSha256, CreatedAtUtc, CorrelationId)
            VALUES
              (@evaluationId, @modelId, @modelVersion, @datasetVersion, @datasetHash,
               @metricsJson, @evidenceSha256, @createdAtUtc, @correlationId);
            """;
        command.Parameters.AddWithValue("@evaluationId", evaluation.EvaluationId);
        command.Parameters.AddWithValue("@modelId", evaluation.ModelId);
        command.Parameters.AddWithValue("@modelVersion", evaluation.ModelVersion);
        command.Parameters.AddWithValue("@datasetVersion", evaluation.DatasetVersion);
        command.Parameters.AddWithValue("@datasetHash", evaluation.DatasetHash);
        command.Parameters.AddWithValue("@metricsJson", evaluation.MetricsJson);
        command.Parameters.AddWithValue("@evidenceSha256", evaluation.EvidenceSha256);
        command.Parameters.AddWithValue("@createdAtUtc", evaluation.CreatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@correlationId", evaluation.CorrelationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AiModelEvaluation>> ListAsync(string modelId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@limit) EvaluationId, ModelId, ModelVersion, DatasetVersion, DatasetHash,
                   MetricsJson, EvidenceSha256, CreatedAtUtc, CorrelationId
            FROM Wcs_AiModelEvaluation
            WHERE ModelId=@modelId
            ORDER BY CreatedAtUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@modelId", modelId.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AiModelEvaluation>();
        while (await reader.ReadAsync(ct))
            result.Add(new AiModelEvaluation(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                SqlModelRegistry.AsUtc(reader.GetDateTime(7)), reader.GetString(8)));
        return result;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

public sealed class SqlModelDriftStore : IModelDriftStore
{
    private readonly string _connectionString;

    public SqlModelDriftStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public async Task AppendAsync(AiModelDriftEvent driftEvent, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Wcs_AiModelDriftEvent
              (DriftEventId, ModelId, ModelVersion, DriftKind, ObservedValue, Threshold,
               OccurredAtUtc, EvidenceSha256, CorrelationId)
            VALUES
              (@driftEventId, @modelId, @modelVersion, @driftKind, @observedValue, @threshold,
               @occurredAtUtc, @evidenceSha256, @correlationId);
            """;
        command.Parameters.AddWithValue("@driftEventId", driftEvent.DriftEventId);
        command.Parameters.AddWithValue("@modelId", driftEvent.ModelId);
        command.Parameters.AddWithValue("@modelVersion", driftEvent.ModelVersion);
        command.Parameters.AddWithValue("@driftKind", driftEvent.DriftKind);
        command.Parameters.AddWithValue("@observedValue", driftEvent.ObservedValue);
        command.Parameters.AddWithValue("@threshold", driftEvent.Threshold);
        command.Parameters.AddWithValue("@occurredAtUtc", driftEvent.OccurredAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@evidenceSha256", driftEvent.EvidenceSha256);
        command.Parameters.AddWithValue("@correlationId", driftEvent.CorrelationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AiModelDriftEvent>> ListAsync(string modelId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@limit) DriftEventId, ModelId, ModelVersion, DriftKind, ObservedValue,
                   Threshold, OccurredAtUtc, EvidenceSha256, CorrelationId
            FROM Wcs_AiModelDriftEvent
            WHERE ModelId=@modelId
            ORDER BY OccurredAtUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@modelId", modelId.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<AiModelDriftEvent>();
        while (await reader.ReadAsync(ct))
            result.Add(new AiModelDriftEvent(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDouble(4), reader.GetDouble(5), SqlModelRegistry.AsUtc(reader.GetDateTime(6)),
                reader.GetString(7), reader.GetString(8)));
        return result;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
