namespace Wcs.Infrastructure.IndustrialIntelligence;

using System.Text.Json;
using SqlSugar;
using Wcs.IndustrialIntelligence.Governance;

[SugarTable("Wcs_BoundedAutomationReadinessEvidence")]
public sealed class BoundedAutomationReadinessEvidenceEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 80, IsNullable = false)]
    public string EvaluationId { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime EvaluatedAtUtc { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string EnvironmentName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int RequestedLevel { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string PolicyVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string PolicyHash { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string SoftwareHeadSha { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string SourceEvidenceHash { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string DecisionHash { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public bool SoftwareSideReady { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool ProductionEnablementAllowed { get; set; }

    [SugarColumn(Length = 120, IsNullable = false)]
    public string Claim { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string ReasonsJson { get; set; } = "[]";
}

public static class BoundedAutomationReadinessSchema
{
    public static void Ensure(SqlSugarClient db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.CodeFirst.InitTables(typeof(BoundedAutomationReadinessEvidenceEntity));
        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Wcs_BoundedAutomationReadiness_EvaluationId' AND object_id = OBJECT_ID('Wcs_BoundedAutomationReadinessEvidence'))
    CREATE UNIQUE INDEX UX_Wcs_BoundedAutomationReadiness_EvaluationId ON Wcs_BoundedAutomationReadinessEvidence(EvaluationId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_BoundedAutomationReadiness_EvaluatedAt' AND object_id = OBJECT_ID('Wcs_BoundedAutomationReadinessEvidence'))
    CREATE INDEX IX_Wcs_BoundedAutomationReadiness_EvaluatedAt ON Wcs_BoundedAutomationReadinessEvidence(EvaluatedAtUtc DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_BoundedAutomationReadiness_Head' AND object_id = OBJECT_ID('Wcs_BoundedAutomationReadinessEvidence'))
    CREATE INDEX IX_Wcs_BoundedAutomationReadiness_Head ON Wcs_BoundedAutomationReadinessEvidence(SoftwareHeadSha, EvaluatedAtUtc DESC);");
    }
}

public sealed class SqlBoundedAutomationReadinessEvidenceStore : IBoundedAutomationReadinessEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqlBoundedAutomationReadinessEvidenceStore(string connectionString) =>
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;

    public async Task AppendAsync(BoundedAutomationReadinessEvidenceRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(record);
        using var db = CreateDb();
        var existing = await db.Queryable<BoundedAutomationReadinessEvidenceEntity>()
            .Where(x => x.EvaluationId == record.EvaluationId)
            .FirstAsync();
        if (existing is not null)
        {
            if (!string.Equals(existing.DecisionHash, record.DecisionHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("EvaluationId is immutable and already belongs to different readiness evidence.");
            return;
        }

        await db.Insertable(ToEntity(record)).ExecuteCommandAsync();
    }

    public async Task<BoundedAutomationReadinessEvidenceRecord?> GetAsync(string evaluationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(evaluationId)) return null;
        using var db = CreateDb();
        var row = await db.Queryable<BoundedAutomationReadinessEvidenceEntity>()
            .Where(x => x.EvaluationId == evaluationId.Trim())
            .FirstAsync();
        return row is null ? null : FromEntity(row);
    }

    public async Task<IReadOnlyList<BoundedAutomationReadinessEvidenceRecord>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = CreateDb();
        var rows = await db.Queryable<BoundedAutomationReadinessEvidenceEntity>()
            .OrderBy(x => x.EvaluatedAtUtc, OrderByType.Desc)
            .Take(limit)
            .ToListAsync();
        return rows.Select(FromEntity).ToArray();
    }

    private static BoundedAutomationReadinessEvidenceEntity ToEntity(BoundedAutomationReadinessEvidenceRecord record) => new()
    {
        EvaluationId = record.EvaluationId,
        EvaluatedAtUtc = record.EvaluatedAtUtc.UtcDateTime,
        EnvironmentName = record.EnvironmentName,
        RequestedLevel = (int)record.RequestedLevel,
        PolicyVersion = record.PolicyVersion,
        PolicyHash = record.PolicyHash,
        SoftwareHeadSha = record.SoftwareHeadSha,
        SourceEvidenceHash = record.SourceEvidenceHash,
        DecisionHash = record.DecisionHash,
        SoftwareSideReady = record.SoftwareSideReady,
        ProductionEnablementAllowed = false,
        Claim = BoundedAutomationReadinessGovernance.SoftwareOnlyClaim,
        ReasonsJson = JsonSerializer.Serialize(record.Reasons, JsonOptions)
    };

    private static BoundedAutomationReadinessEvidenceRecord FromEntity(BoundedAutomationReadinessEvidenceEntity row)
    {
        var reasons = JsonSerializer.Deserialize<string[]>(row.ReasonsJson, JsonOptions) ?? Array.Empty<string>();
        var record = new BoundedAutomationReadinessEvidenceRecord(
            row.EvaluationId,
            new DateTimeOffset(DateTime.SpecifyKind(row.EvaluatedAtUtc, DateTimeKind.Utc)),
            row.EnvironmentName,
            (AutomationLevel)row.RequestedLevel,
            row.PolicyVersion,
            row.PolicyHash,
            row.SoftwareHeadSha,
            row.SourceEvidenceHash,
            row.DecisionHash,
            row.SoftwareSideReady,
            row.ProductionEnablementAllowed,
            row.Claim,
            reasons);
        Validate(record);
        return record;
    }

    private static void Validate(BoundedAutomationReadinessEvidenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.EvaluationId) || record.EvaluationId.Length > 80 ||
            string.IsNullOrWhiteSpace(record.EnvironmentName) || !Hashing.IsSha256(record.PolicyHash) ||
            !BoundedAutomationReadinessGovernance.IsGitCommitSha(record.SoftwareHeadSha) ||
            !Hashing.IsSha256(record.SourceEvidenceHash) || !Hashing.IsSha256(record.DecisionHash) ||
            record.ProductionEnablementAllowed ||
            !string.Equals(record.Claim, BoundedAutomationReadinessGovernance.SoftwareOnlyClaim, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted P6 readiness evidence violates fail-closed invariants.");
    }

    private SqlSugarClient CreateDb() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}

public sealed class BoundedAutomationReadinessPersistenceFactory
{
    private static readonly object SchemaSync = new();
    private static readonly HashSet<string> InitializedConnectionStrings = new(StringComparer.Ordinal);
    private readonly string _connectionString;

    public BoundedAutomationReadinessPersistenceFactory(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public void EnsureSchema()
    {
        lock (SchemaSync)
        {
            if (InitializedConnectionStrings.Contains(_connectionString)) return;
            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true
            });
            BoundedAutomationReadinessSchema.Ensure(db);
            InitializedConnectionStrings.Add(_connectionString);
        }
    }

    public IBoundedAutomationReadinessEvidenceStore CreateStore() => new SqlBoundedAutomationReadinessEvidenceStore(_connectionString);
}
