namespace Wcs.Infrastructure.IndustrialIntelligence;

using Microsoft.Data.SqlClient;
using Wcs.FeatureCenter;

public interface IFeatureQualityEventStore
{
    Task AppendAsync(FeatureQualityEvent qualityEvent, CancellationToken ct);
    Task<IReadOnlyList<FeatureQualityEvent>> ListAsync(string entityId, int limit, CancellationToken ct);
}

public sealed class SqlFeatureQualityEventStore : IFeatureQualityEventStore
{
    private readonly string _connectionString;
    public SqlFeatureQualityEventStore(string connectionString) => _connectionString = Require(connectionString);

    public async Task AppendAsync(FeatureQualityEvent qualityEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(qualityEvent);
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = @"IF NOT EXISTS(SELECT 1 FROM Wcs_FeatureQualityEvent WHERE QualityEventId=@id)
INSERT INTO Wcs_FeatureQualityEvent(QualityEventId,EntityId,FeatureId,Status,Reason,OccurredAtUtc,EvidenceSha256,CorrelationId)
VALUES(@id,@entity,@feature,@status,@reason,@utc,@sha,@correlation)";
        command.Parameters.AddWithValue("@id", qualityEvent.QualityEventId); command.Parameters.AddWithValue("@entity", qualityEvent.EntityId);
        command.Parameters.AddWithValue("@feature", qualityEvent.FeatureId); command.Parameters.AddWithValue("@status", qualityEvent.Status.ToString());
        command.Parameters.AddWithValue("@reason", qualityEvent.Reason); command.Parameters.AddWithValue("@utc", qualityEvent.OccurredAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("@sha", qualityEvent.EvidenceSha256); command.Parameters.AddWithValue("@correlation", qualityEvent.CorrelationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FeatureQualityEvent>> ListAsync(string entityId, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TOP (@limit) QualityEventId,EntityId,FeatureId,Status,Reason,OccurredAtUtc,EvidenceSha256,CorrelationId FROM Wcs_FeatureQualityEvent WHERE EntityId=@entity ORDER BY OccurredAtUtc DESC,Id DESC";
        command.Parameters.AddWithValue("@limit", limit); command.Parameters.AddWithValue("@entity", entityId.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<FeatureQualityEvent>();
        while (await reader.ReadAsync(ct)) result.Add(new FeatureQualityEvent(reader.GetString(0),reader.GetString(1),reader.GetString(2),Enum.Parse<FeatureQualityStatus>(reader.GetString(3),true),reader.GetString(4),Utc(reader.GetDateTime(5)),reader.GetString(6),reader.GetString(7)));
        return result;
    }

    private static string Require(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Connection string is required.") : value;
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public interface IFeatureDatasetStore
{
    Task SaveAsync(FeatureDatasetManifest manifest, CancellationToken ct);
    Task<FeatureDatasetManifest?> GetAsync(string datasetId, string version, CancellationToken ct);
}

public sealed class SqlFeatureDatasetStore : IFeatureDatasetStore
{
    private readonly string _connectionString;
    public SqlFeatureDatasetStore(string connectionString) => _connectionString = string.IsNullOrWhiteSpace(connectionString) ? throw new ArgumentException("Connection string is required.") : connectionString;

    public async Task SaveAsync(FeatureDatasetManifest m, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m); if (m.RowCount < 0) throw new ArgumentOutOfRangeException(nameof(m.RowCount)); if (m.ToUtc < m.FromUtc) throw new InvalidOperationException("Dataset ToUtc must be >= FromUtc.");
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = @"IF EXISTS(SELECT 1 FROM Wcs_FeatureDataset WHERE DatasetId=@id AND Version=@version AND DatasetHash<>@hash) THROW 51000,'Dataset version is immutable.',1;
IF NOT EXISTS(SELECT 1 FROM Wcs_FeatureDataset WHERE DatasetId=@id AND Version=@version)
INSERT INTO Wcs_FeatureDataset(DatasetId,Version,FeatureSchemaId,FeatureSchemaHash,FromUtc,ToUtc,RowCount,DatasetHash,StorageUri,StorageSha256,CreatedAtUtc,CreatedBy,CorrelationId)
VALUES(@id,@version,@schema,@schemahash,@from,@to,@rows,@hash,@uri,@sha,@created,@by,@correlation)";
        command.Parameters.AddWithValue("@id",m.DatasetId); command.Parameters.AddWithValue("@version",m.Version); command.Parameters.AddWithValue("@schema",m.FeatureSchemaId); command.Parameters.AddWithValue("@schemahash",m.FeatureSchemaHash);
        command.Parameters.AddWithValue("@from",m.FromUtc.UtcDateTime); command.Parameters.AddWithValue("@to",m.ToUtc.UtcDateTime); command.Parameters.AddWithValue("@rows",m.RowCount); command.Parameters.AddWithValue("@hash",m.DatasetHash);
        command.Parameters.AddWithValue("@uri",m.StorageUri); command.Parameters.AddWithValue("@sha",m.StorageSha256); command.Parameters.AddWithValue("@created",m.CreatedAtUtc.UtcDateTime); command.Parameters.AddWithValue("@by",m.CreatedBy); command.Parameters.AddWithValue("@correlation",m.CorrelationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<FeatureDatasetManifest?> GetAsync(string datasetId, string version, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FeatureSchemaId,FeatureSchemaHash,FromUtc,ToUtc,RowCount,DatasetHash,StorageUri,StorageSha256,CreatedAtUtc,CreatedBy,CorrelationId FROM Wcs_FeatureDataset WHERE DatasetId=@id AND Version=@version";
        command.Parameters.AddWithValue("@id",datasetId.Trim()); command.Parameters.AddWithValue("@version",version.Trim()); await using var r=await command.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return null;
        return new FeatureDatasetManifest(datasetId.Trim(),version.Trim(),r.GetString(0),r.GetString(1),Utc(r.GetDateTime(2)),Utc(r.GetDateTime(3)),r.GetInt64(4),r.GetString(5),r.GetString(6),r.GetString(7),Utc(r.GetDateTime(8)),r.GetString(9),r.GetString(10));
    }
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public interface IFeatureLineageStore
{
    Task AppendAsync(FeatureLineageEntry entry, CancellationToken ct);
    Task<IReadOnlyList<FeatureLineageEntry>> ListForOutputAsync(string outputId, int limit, CancellationToken ct);
}

public sealed class SqlFeatureLineageStore : IFeatureLineageStore
{
    private readonly string _connectionString;
    public SqlFeatureLineageStore(string connectionString) => _connectionString = string.IsNullOrWhiteSpace(connectionString) ? throw new ArgumentException("Connection string is required.") : connectionString;
    public async Task AppendAsync(FeatureLineageEntry e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e); await using var connection=new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command=connection.CreateCommand();
        command.CommandText=@"IF NOT EXISTS(SELECT 1 FROM Wcs_FeatureLineage WHERE LineageId=@id) INSERT INTO Wcs_FeatureLineage(LineageId,OutputId,OutputType,SourceType,SourceId,AsOfUtc,TransformationVersion,CorrelationId) VALUES(@id,@output,@outputType,@sourceType,@source,@asof,@transform,@correlation)";
        command.Parameters.AddWithValue("@id",e.LineageId); command.Parameters.AddWithValue("@output",e.OutputId); command.Parameters.AddWithValue("@outputType",e.OutputType); command.Parameters.AddWithValue("@sourceType",e.SourceType); command.Parameters.AddWithValue("@source",e.SourceId); command.Parameters.AddWithValue("@asof",e.AsOfUtc.UtcDateTime); command.Parameters.AddWithValue("@transform",e.TransformationVersion); command.Parameters.AddWithValue("@correlation",e.CorrelationId); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<FeatureLineageEntry>> ListForOutputAsync(string outputId,int limit,CancellationToken ct)
    {
        if(limit is <1 or >10_000) throw new ArgumentOutOfRangeException(nameof(limit)); await using var connection=new SqlConnection(_connectionString); await connection.OpenAsync(ct); await using var command=connection.CreateCommand(); command.CommandText="SELECT TOP (@limit) LineageId,OutputId,OutputType,SourceType,SourceId,AsOfUtc,TransformationVersion,CorrelationId FROM Wcs_FeatureLineage WHERE OutputId=@output ORDER BY AsOfUtc,Id"; command.Parameters.AddWithValue("@limit",limit); command.Parameters.AddWithValue("@output",outputId.Trim()); await using var r=await command.ExecuteReaderAsync(ct); var result=new List<FeatureLineageEntry>(); while(await r.ReadAsync(ct)) result.Add(new FeatureLineageEntry(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(5),DateTimeKind.Utc)),r.GetString(6),r.GetString(7))); return result;
    }
}
