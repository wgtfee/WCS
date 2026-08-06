namespace Wcs.Infrastructure.IndustrialIntelligence;

using System.Data;
using Microsoft.Data.SqlClient;
using Wcs.MaintenanceLearning;

public sealed class SqlMaintenanceEvaluationWindowStore : IMaintenanceEvaluationWindowStore
{
    private readonly string _connectionString;
    public SqlMaintenanceEvaluationWindowStore(string connectionString) => _connectionString = Require(connectionString);

    public async Task SaveAsync(VersionedEvaluationWindow window, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Validate();
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = "SELECT DefinitionHash FROM Wcs_MaintenanceEvaluationWindow WITH (UPDLOCK,HOLDLOCK) WHERE AssetType=@assetType AND Version=@version;";
            select.Parameters.AddWithValue("@assetType", window.AssetType);
            select.Parameters.AddWithValue("@version", window.Version);
            var existing = await select.ExecuteScalarAsync(ct);
            if (existing is not null && existing is not DBNull)
            {
                if (!string.Equals(Convert.ToString(existing), window.DefinitionHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("EvaluationWindow version is immutable and already exists with a different DefinitionHash.");
                await transaction.CommitAsync(ct);
                return;
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Wcs_MaintenanceEvaluationWindow
                  (AssetType,Version,ImmediateTicks,ShortTicks,MediumTicks,LongTicks,ApprovedBy,ApprovedAtUtc,DefinitionHash)
                VALUES
                  (@assetType,@version,@immediate,@short,@medium,@long,@approvedBy,@approvedAt,@definitionHash);
                """;
            insert.Parameters.AddWithValue("@assetType", window.AssetType);
            insert.Parameters.AddWithValue("@version", window.Version);
            insert.Parameters.AddWithValue("@immediate", window.ImmediateWindow.Ticks);
            insert.Parameters.AddWithValue("@short", window.ShortWindow.Ticks);
            insert.Parameters.AddWithValue("@medium", window.MediumWindow.Ticks);
            insert.Parameters.AddWithValue("@long", window.LongWindow.Ticks);
            insert.Parameters.AddWithValue("@approvedBy", window.ApprovedBy);
            insert.Parameters.AddWithValue("@approvedAt", window.ApprovedAtUtc.UtcDateTime);
            insert.Parameters.AddWithValue("@definitionHash", window.DefinitionHash);
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<VersionedEvaluationWindow?> GetAsync(string assetType, string version, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AssetType,Version,ImmediateTicks,ShortTicks,MediumTicks,LongTicks,ApprovedBy,ApprovedAtUtc,DefinitionHash
            FROM Wcs_MaintenanceEvaluationWindow WHERE AssetType=@assetType AND Version=@version;
            """;
        command.Parameters.AddWithValue("@assetType", assetType);
        command.Parameters.AddWithValue("@version", version);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return null;
        var value = new VersionedEvaluationWindow(
            reader.GetString(0), reader.GetString(1), TimeSpan.FromTicks(reader.GetInt64(2)), TimeSpan.FromTicks(reader.GetInt64(3)),
            TimeSpan.FromTicks(reader.GetInt64(4)), TimeSpan.FromTicks(reader.GetInt64(5)), reader.GetString(6), Utc(reader.GetDateTime(7)));
        if (!string.Equals(value.DefinitionHash, reader.GetString(8), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Persisted EvaluationWindow DefinitionHash mismatch.");
        return value;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct) { var c = new SqlConnection(_connectionString); await c.OpenAsync(ct); return c; }
    private static string Require(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Connection string is required.") : value;
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class SqlMaintenanceCausalEvidenceStore : IMaintenanceCausalEvidenceStore
{
    private readonly string _connectionString;
    public SqlMaintenanceCausalEvidenceStore(string connectionString) => _connectionString = string.IsNullOrWhiteSpace(connectionString) ? throw new ArgumentException("Connection string is required.") : connectionString;

    public async Task SaveCandidateAsync(CausalCandidate candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateHash(candidate.EvidenceHash);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM Wcs_MaintenanceCausalCandidate WHERE CandidateId=@candidateId)
            INSERT INTO Wcs_MaintenanceCausalCandidate(CandidateId,InterventionId,Treatment,OutcomeMetric,EvidenceHash)
            VALUES(@candidateId,@interventionId,@treatment,@outcomeMetric,@evidenceHash);
            """;
        command.Parameters.AddWithValue("@candidateId", candidate.CandidateId);
        command.Parameters.AddWithValue("@interventionId", candidate.InterventionId);
        command.Parameters.AddWithValue("@treatment", candidate.Treatment);
        command.Parameters.AddWithValue("@outcomeMetric", candidate.OutcomeMetric);
        command.Parameters.AddWithValue("@evidenceHash", candidate.EvidenceHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveCounterfactualAsync(CounterfactualEstimate estimate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ValidateHash(estimate.EvidenceHash);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            MERGE Wcs_MaintenanceCounterfactualEstimate AS target
            USING (SELECT @candidateId AS CandidateId) source ON target.CandidateId=source.CandidateId
            WHEN MATCHED THEN UPDATE SET ObservedValue=@observed,CounterfactualValue=@counterfactual,EstimatedEffect=@effect,MethodVersion=@methodVersion,EvidenceHash=@evidenceHash
            WHEN NOT MATCHED THEN INSERT(CandidateId,ObservedValue,CounterfactualValue,EstimatedEffect,MethodVersion,EvidenceHash)
              VALUES(@candidateId,@observed,@counterfactual,@effect,@methodVersion,@evidenceHash);
            """;
        command.Parameters.AddWithValue("@candidateId", estimate.CandidateId);
        command.Parameters.AddWithValue("@observed", estimate.ObservedValue);
        command.Parameters.AddWithValue("@counterfactual", estimate.CounterfactualValue);
        command.Parameters.AddWithValue("@effect", estimate.EstimatedEffect);
        command.Parameters.AddWithValue("@methodVersion", estimate.MethodVersion);
        command.Parameters.AddWithValue("@evidenceHash", estimate.EvidenceHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CausalCandidate>> ListCandidatesAsync(string interventionId, int take = 100, CancellationToken ct = default)
    {
        if (take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(take));
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (@take) CandidateId,InterventionId,Treatment,OutcomeMetric,EvidenceHash
            FROM Wcs_MaintenanceCausalCandidate WHERE InterventionId=@interventionId ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@take", take);
        command.Parameters.AddWithValue("@interventionId", interventionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<CausalCandidate>();
        while (await reader.ReadAsync(ct)) values.Add(new CausalCandidate(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));
        return values;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct) { var c = new SqlConnection(_connectionString); await c.OpenAsync(ct); return c; }
    private static void ValidateHash(string value) { if (value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new ArgumentException("EvidenceHash must be SHA-256 hex."); }
}
