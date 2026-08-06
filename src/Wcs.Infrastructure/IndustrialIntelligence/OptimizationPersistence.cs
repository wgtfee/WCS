namespace Wcs.Infrastructure.IndustrialIntelligence;

using System.Text.Json;
using SqlSugar;
using Wcs.Optimization;

public sealed class SqlOptimizationExperimentStore : IOptimizationExperimentStore, IOptimizationRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqlOptimizationExperimentStore(string connectionString) =>
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;

    public async Task SaveDefinitionAsync(OptimizationExperimentDefinition definition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DigitalTwinOptimizer.ValidateDefinition(definition);
        using var db = CreateDb();
        var existing = await db.Queryable<OptimizationExperimentEntity>()
            .Where(x => x.ExperimentId == definition.ExperimentId)
            .FirstAsync();
        if (existing is not null)
        {
            if (!string.Equals(existing.DefinitionHash, definition.DefinitionHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ExperimentId is immutable and already belongs to a different DefinitionHash.");
            return;
        }

        await db.Insertable(new OptimizationExperimentEntity
        {
            ExperimentId = definition.ExperimentId,
            DefinitionHash = definition.DefinitionHash,
            SoftwareHead = definition.SoftwareHead,
            DefinitionJson = JsonSerializer.Serialize(definition, JsonOptions),
            Status = "Defined",
            CreatedAtUtc = DateTime.UtcNow
        }).ExecuteCommandAsync();
    }

    public async Task SaveResultAsync(OptimizationExperimentResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OptimizationHash.IsSha256(result.DefinitionHash) || !OptimizationHash.IsSha256(result.EvidenceHash) ||
            result.ControlWriteAllowed || result.AutoProductionPolicyReplacementAllowed)
            throw new InvalidOperationException("Optimization result violates governed evidence/control invariants.");

        using var db = CreateDb();
        var definition = await db.Queryable<OptimizationExperimentEntity>()
            .Where(x => x.ExperimentId == result.ExperimentId)
            .FirstAsync() ?? throw new InvalidOperationException("Experiment definition must be persisted before its result.");
        if (!string.Equals(definition.DefinitionHash, result.DefinitionHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(definition.SoftwareHead, result.SoftwareHead, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Result does not match persisted experiment definition.");

        var existing = await db.Queryable<OptimizationExperimentResultEntity>()
            .Where(x => x.ExperimentId == result.ExperimentId)
            .FirstAsync();
        if (existing is not null)
        {
            if (!string.Equals(existing.EvidenceHash, result.EvidenceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Completed experiment result is immutable.");
            return;
        }

        db.Ado.BeginTran();
        try
        {
            await db.Insertable(new OptimizationExperimentResultEntity
            {
                ExperimentId = result.ExperimentId,
                DefinitionHash = result.DefinitionHash,
                SoftwareHead = result.SoftwareHead,
                EvidenceHash = result.EvidenceHash,
                ResultJson = JsonSerializer.Serialize(result, JsonOptions),
                ControlWriteAllowed = false,
                AutoProductionPolicyReplacementAllowed = false,
                CompletedAtUtc = DateTime.UtcNow
            }).ExecuteCommandAsync();

            foreach (var score in result.Ranking)
            {
                await db.Insertable(new OptimizationPolicyEvidenceEntity
                {
                    ExperimentId = result.ExperimentId,
                    PolicyId = score.PolicyId,
                    PolicyHash = score.PolicyHash,
                    Score = score.Score,
                    ParetoEfficient = score.ParetoEfficient,
                    SuccessfulRuns = score.SuccessfulRuns,
                    FailedRuns = score.FailedRuns
                }).ExecuteCommandAsync();
            }

            definition.Status = "Completed";
            definition.CompletedAtUtc = DateTime.UtcNow;
            await db.Updateable(definition).ExecuteCommandAsync();
            db.Ado.CommitTran();
        }
        catch
        {
            db.Ado.RollbackTran();
            throw;
        }
    }

    public async Task<OptimizationExperimentDefinition?> GetDefinitionAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateDb();
        var row = await db.Queryable<OptimizationExperimentEntity>().Where(x => x.ExperimentId == experimentId).FirstAsync();
        return row is null ? null : JsonSerializer.Deserialize<OptimizationExperimentDefinition>(row.DefinitionJson, JsonOptions);
    }

    public async Task<OptimizationExperimentResult?> GetResultAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateDb();
        var row = await db.Queryable<OptimizationExperimentResultEntity>().Where(x => x.ExperimentId == experimentId).FirstAsync();
        return row is null ? null : JsonSerializer.Deserialize<OptimizationExperimentResult>(row.ResultJson, JsonOptions);
    }

    public async Task<IReadOnlyList<OptimizationExperimentSummary>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = CreateDb();
        var rows = await db.Queryable<OptimizationExperimentEntity>().OrderBy(x => x.CreatedAtUtc, OrderByType.Desc).Take(limit).ToListAsync();
        var resultRows = await db.Queryable<OptimizationExperimentResultEntity>().Where(x => rows.Select(r => r.ExperimentId).Contains(x.ExperimentId)).ToListAsync();
        var byExperiment = resultRows.ToDictionary(x => x.ExperimentId, StringComparer.Ordinal);
        return rows.Select(row =>
        {
            byExperiment.TryGetValue(row.ExperimentId, out var result);
            return new OptimizationExperimentSummary(row.ExperimentId, row.DefinitionHash, row.SoftwareHead, row.Status,
                row.CreatedAtUtc, row.CompletedAtUtc, result?.EvidenceHash,
                result?.ControlWriteAllowed ?? false, result?.AutoProductionPolicyReplacementAllowed ?? false);
        }).ToArray();
    }

    public async Task<OptimizationRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateDb();
        var definitions = await db.Queryable<OptimizationExperimentEntity>().ToListAsync();
        var results = await db.Queryable<OptimizationExperimentResultEntity>().ToListAsync();
        var errors = new List<string>();
        foreach (var row in definitions)
        {
            try
            {
                var definition = JsonSerializer.Deserialize<OptimizationExperimentDefinition>(row.DefinitionJson, JsonOptions)
                    ?? throw new InvalidOperationException("DefinitionJson is empty.");
                DigitalTwinOptimizer.ValidateDefinition(definition);
                if (!string.Equals(definition.DefinitionHash, row.DefinitionHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(definition.SoftwareHead, row.SoftwareHead, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Persisted definition hash/head mismatch.");
            }
            catch (Exception ex)
            {
                errors.Add($"{row.ExperimentId}: {ex.Message}");
            }
        }

        foreach (var result in results)
        {
            if (result.ControlWriteAllowed || result.AutoProductionPolicyReplacementAllowed || !OptimizationHash.IsSha256(result.EvidenceHash))
                errors.Add($"{result.ExperimentId}: persisted result violates zero-control evidence invariants.");
        }

        return new OptimizationRecoveryResult(definitions.Count, results.Count, errors.Count, errors.Count == 0, errors);
    }

    private SqlSugarClient CreateDb() => new(new ConnectionConfig
    {
        ConnectionString = _connectionString,
        DbType = DbType.SqlServer,
        IsAutoCloseConnection = true
    });
}

public sealed class OptimizationPersistenceFactory
{
    private static readonly object SchemaSync = new();
    private static readonly HashSet<string> InitializedConnectionStrings = new(StringComparer.Ordinal);
    private readonly string _connectionString;

    public OptimizationPersistenceFactory(string connectionString)
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
            OptimizationSchema.Ensure(db);
            InitializedConnectionStrings.Add(_connectionString);
        }
    }

    public IOptimizationExperimentStore CreateStore() => new SqlOptimizationExperimentStore(_connectionString);
    public IOptimizationRecoveryService CreateRecovery() => new SqlOptimizationExperimentStore(_connectionString);
}
