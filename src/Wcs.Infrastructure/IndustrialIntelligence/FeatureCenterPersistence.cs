namespace Wcs.Infrastructure.IndustrialIntelligence;

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Wcs.FeatureCenter;

public sealed class SqlFeatureDefinitionRegistry : IFeatureDefinitionRegistry
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public SqlFeatureDefinitionRegistry(string connectionString) => _connectionString = Require(connectionString);

    public async Task RegisterAsync(FeatureDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(FeatureDefinitionHash.Compute(definition), definition.DefinitionHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FeatureDefinition hash is invalid.");
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var select = connection.CreateCommand(); select.Transaction = transaction;
            select.CommandText = "SELECT DefinitionHash FROM Wcs_FeatureDefinition WITH (UPDLOCK,HOLDLOCK) WHERE FeatureId=@id AND Version=@version";
            select.Parameters.AddWithValue("@id", definition.FeatureId); select.Parameters.AddWithValue("@version", definition.Version);
            var existing = await select.ExecuteScalarAsync(ct);
            if (existing is not null && existing is not DBNull)
            {
                if (!string.Equals(Convert.ToString(existing), definition.DefinitionHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The same FeatureId + Version cannot be registered with a different DefinitionHash.");
                await transaction.CommitAsync(ct); return;
            }
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO Wcs_FeatureDefinition(FeatureId,Version,DefinitionHash,DefinitionJson,CreatedAtUtc) VALUES(@id,@version,@hash,@json,@utc)";
            insert.Parameters.AddWithValue("@id", definition.FeatureId); insert.Parameters.AddWithValue("@version", definition.Version);
            insert.Parameters.AddWithValue("@hash", definition.DefinitionHash); insert.Parameters.AddWithValue("@json", JsonSerializer.Serialize(definition, _json));
            insert.Parameters.AddWithValue("@utc", DateTime.UtcNow); await insert.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<FeatureDefinition?> GetAsync(string featureId, string version, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DefinitionJson FROM Wcs_FeatureDefinition WHERE FeatureId=@id AND Version=@version";
        command.Parameters.AddWithValue("@id", featureId.Trim()); command.Parameters.AddWithValue("@version", version.Trim());
        var json = await command.ExecuteScalarAsync(ct) as string; return json is null ? null : JsonSerializer.Deserialize<FeatureDefinition>(json, _json);
    }

    public async Task<IReadOnlyList<FeatureDefinition>> ListAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DefinitionJson FROM Wcs_FeatureDefinition ORDER BY FeatureId,Version";
        await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<FeatureDefinition>();
        while (await reader.ReadAsync(ct)) { var item = JsonSerializer.Deserialize<FeatureDefinition>(reader.GetString(0), _json); if (item is not null) result.Add(item); }
        return result;
    }
    private async Task<SqlConnection> OpenAsync(CancellationToken ct) { var c = new SqlConnection(_connectionString); await c.OpenAsync(ct); return c; }
    private static string Require(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Connection string is required.") : value;
}

public sealed class SqlFeatureSchemaRegistry : IFeatureSchemaRegistry
{
    private readonly string _connectionString;
    private readonly IFeatureDefinitionRegistry _definitions;
    public SqlFeatureSchemaRegistry(string connectionString, IFeatureDefinitionRegistry definitions) { _connectionString = string.IsNullOrWhiteSpace(connectionString) ? throw new ArgumentException("Connection string is required.") : connectionString; _definitions = definitions; }

    public async Task RegisterAsync(FeatureSchema schema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!string.Equals(FeatureSchemaHash.Compute(schema), schema.SchemaHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("FeatureSchema hash is invalid.");
        var definitions = await _definitions.ListAsync(ct);
        foreach (var item in schema.Items) if (!definitions.Any(x => x.FeatureId.Equals(item.FeatureId, StringComparison.OrdinalIgnoreCase) && x.DefinitionHash.Equals(item.DefinitionHash, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Feature definition '{item.FeatureId}' with required hash is not registered.");
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using var select = connection.CreateCommand(); select.Transaction = transaction; select.CommandText = "SELECT SchemaHash FROM Wcs_FeatureSchema WITH(UPDLOCK,HOLDLOCK) WHERE SchemaId=@id AND Version=@version"; select.Parameters.AddWithValue("@id", schema.SchemaId); select.Parameters.AddWithValue("@version", schema.Version);
            var existing = await select.ExecuteScalarAsync(ct);
            if (existing is not null && existing is not DBNull) { if (!string.Equals(Convert.ToString(existing), schema.SchemaHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The same SchemaId + Version cannot have a different SchemaHash."); await transaction.CommitAsync(ct); return; }
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction; insert.CommandText = "INSERT INTO Wcs_FeatureSchema(SchemaId,Version,SchemaHash,Status,ApprovedBy,ApprovedAtUtc) VALUES(@id,@version,@hash,@status,@by,@at)"; insert.Parameters.AddWithValue("@id", schema.SchemaId); insert.Parameters.AddWithValue("@version", schema.Version); insert.Parameters.AddWithValue("@hash", schema.SchemaHash); insert.Parameters.AddWithValue("@status", schema.Status.ToString()); insert.Parameters.AddWithValue("@by", schema.ApprovedBy ?? string.Empty); insert.Parameters.AddWithValue("@at", schema.ApprovedAtUtc?.UtcDateTime ?? (object)DBNull.Value); await insert.ExecuteNonQueryAsync(ct);
            foreach (var item in schema.Items) { await using var child = connection.CreateCommand(); child.Transaction = transaction; child.CommandText = "INSERT INTO Wcs_FeatureSchemaItem(SchemaId,SchemaVersion,FeatureId,DefinitionHash,Ordinal) VALUES(@id,@version,@feature,@hash,@ordinal)"; child.Parameters.AddWithValue("@id", schema.SchemaId); child.Parameters.AddWithValue("@version", schema.Version); child.Parameters.AddWithValue("@feature", item.FeatureId); child.Parameters.AddWithValue("@hash", item.DefinitionHash); child.Parameters.AddWithValue("@ordinal", item.Ordinal); await child.ExecuteNonQueryAsync(ct); }
            await transaction.CommitAsync(ct);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<FeatureSchema?> GetAsync(string schemaId, string version, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command = connection.CreateCommand(); command.CommandText = "SELECT SchemaHash,Status,ApprovedBy,ApprovedAtUtc FROM Wcs_FeatureSchema WHERE SchemaId=@id AND Version=@version"; command.Parameters.AddWithValue("@id", schemaId.Trim()); command.Parameters.AddWithValue("@version", version.Trim());
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct); if (!await reader.ReadAsync(ct)) return null;
        var hash = reader.GetString(0); var status = Enum.Parse<FeatureSchemaStatus>(reader.GetString(1), true); var by = reader.GetString(2); DateTimeOffset? at = reader.IsDBNull(3) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)); await reader.CloseAsync();
        await using var itemsCommand = connection.CreateCommand(); itemsCommand.CommandText = "SELECT FeatureId,DefinitionHash,Ordinal FROM Wcs_FeatureSchemaItem WHERE SchemaId=@id AND SchemaVersion=@version ORDER BY Ordinal"; itemsCommand.Parameters.AddWithValue("@id", schemaId.Trim()); itemsCommand.Parameters.AddWithValue("@version", version.Trim()); await using var itemsReader = await itemsCommand.ExecuteReaderAsync(ct); var items = new List<FeatureSchemaItem>(); while (await itemsReader.ReadAsync(ct)) items.Add(new FeatureSchemaItem(itemsReader.GetString(0), itemsReader.GetString(1), itemsReader.GetInt32(2)));
        return new FeatureSchema(schemaId.Trim(), version.Trim(), hash, status, items, by, at);
    }
}

public interface IFeatureSnapshotStore
{
    Task SaveAsync(FeatureSnapshot snapshot, CancellationToken ct);
    Task<FeatureSnapshot?> GetAsync(string snapshotId, CancellationToken ct);
}

public sealed class SqlFeatureSnapshotStore : IFeatureSnapshotStore
{
    private readonly string _connectionString; private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public SqlFeatureSnapshotStore(string connectionString) => _connectionString = string.IsNullOrWhiteSpace(connectionString) ? throw new ArgumentException("Connection string is required.") : connectionString;
    public async Task SaveAsync(FeatureSnapshot snapshot, CancellationToken ct)
    {
        if (!string.Equals(FeatureSnapshotHash.ComputeValuesHash(snapshot.Values), snapshot.ValuesHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Snapshot ValuesHash is invalid.");
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command = connection.CreateCommand(); command.CommandText = "IF NOT EXISTS(SELECT 1 FROM Wcs_FeatureSnapshot WHERE SnapshotId=@id) INSERT INTO Wcs_FeatureSnapshot(SnapshotId,EntityId,AsOfUtc,FeatureSchemaId,FeatureSchemaHash,ValuesJson,SourceOffsetsJson,ValuesHash,QualityStatus,MaterializerVersion) VALUES(@id,@entity,@asof,@schema,@schemahash,@values,@offsets,@hash,@quality,@materializer)"; command.Parameters.AddWithValue("@id", snapshot.SnapshotId); command.Parameters.AddWithValue("@entity", snapshot.EntityId); command.Parameters.AddWithValue("@asof", snapshot.AsOfUtc.UtcDateTime); command.Parameters.AddWithValue("@schema", snapshot.FeatureSchemaId); command.Parameters.AddWithValue("@schemahash", snapshot.FeatureSchemaHash); command.Parameters.AddWithValue("@values", JsonSerializer.Serialize(snapshot.Values, _json)); command.Parameters.AddWithValue("@offsets", JsonSerializer.Serialize(snapshot.SourceOffsets, _json)); command.Parameters.AddWithValue("@hash", snapshot.ValuesHash); command.Parameters.AddWithValue("@quality", snapshot.QualityStatus.ToString()); command.Parameters.AddWithValue("@materializer", snapshot.MaterializerVersion); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<FeatureSnapshot?> GetAsync(string snapshotId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command = connection.CreateCommand(); command.CommandText = "SELECT EntityId,AsOfUtc,FeatureSchemaId,FeatureSchemaHash,ValuesJson,SourceOffsetsJson,ValuesHash,QualityStatus,MaterializerVersion FROM Wcs_FeatureSnapshot WHERE SnapshotId=@id"; command.Parameters.AddWithValue("@id", snapshotId.Trim()); await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct); if (!await reader.ReadAsync(ct)) return null; var values = JsonSerializer.Deserialize<List<FeatureValue>>(reader.GetString(4), _json) ?? []; var offsets = JsonSerializer.Deserialize<List<FeatureSourceOffset>>(reader.GetString(5), _json) ?? []; return new FeatureSnapshot(snapshotId.Trim(), reader.GetString(0), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)), reader.GetString(2), reader.GetString(3), values, offsets, reader.GetString(6), Enum.Parse<FeatureQualityStatus>(reader.GetString(7), true), reader.GetString(8));
    }
}
