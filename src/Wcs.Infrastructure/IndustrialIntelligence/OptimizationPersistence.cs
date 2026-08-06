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
            ScenarioEvidenceHash = definition.ScenarioEvidenceHash,
            TopologyEvidenceHash = definition.TopologyEvidenceHash,
            OrderDatasetEvidenceHash = definition.OrderDatasetEvidenceHash,
            ObjectiveWeightsEvidenceHash = definition.ObjectiveWeightsEvidenceHash,
            ConstraintProfileHash = definition.ConstraintProfileHash.ToLowerInvariant(),
            DefinitionJson = JsonSerializer.Serialize(definition, JsonOptions),
            Status = "Defined",
            CreatedAtUtc = DateTime.UtcNow
        }).ExecuteCommandAsync();
    }

    public async Task SaveResultAsync(OptimizationExperimentResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateResultEvidence(result);

        using var db = CreateDb();
        var definitionRow = await db.Queryable<OptimizationExperimentEntity>()
            .Where(x => x.ExperimentId == result.ExperimentId)
            .FirstAsync() ?? throw new InvalidOperationException("Experiment definition must be persisted before its result.");
        var definition = JsonSerializer.Deserialize<OptimizationExperimentDefinition>(definitionRow.DefinitionJson, JsonOptions)
            ?? throw new InvalidOperationException("Persisted experiment definition cannot be deserialized.");
        DigitalTwinOptimizer.ValidateDefinition(definition);
        if (!string.Equals(definitionRow.DefinitionHash, result.DefinitionHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(definitionRow.SoftwareHead, result.SoftwareHead, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(definition.DefinitionHash, result.DefinitionHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Result does not match persisted experiment definition.");
        var recomputedEvidence = OptimizationHash.ComputeResultEvidenceHash(definition, result.Runs);
        if (!string.Equals(recomputedEvidence, result.EvidenceHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Result EvidenceHash does not match its governed definition and run evidence.");

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
                ProductionAutomationAllowed = false,
                CompletedAtUtc = DateTime.UtcNow
            }).ExecuteCommandAsync();

            for (var index = 0; index < result.Ranking.Count; index++)
            {
                var score = result.Ranking[index];
                await db.Insertable(new OptimizationPolicyEvidenceEntity
                {
                    ExperimentId = result.ExperimentId,
                    PolicyId = score.PolicyId,
                    PolicyHash = score.PolicyHash,
                    Rank = index + 1,
                    Score = score.Score,
                    ParetoEfficient = score.ParetoEfficient,
                    HardConstraintQualified = score.HardConstraintQualified,
                    SuccessfulRuns = score.SuccessfulRuns,
                    FailedRuns = score.FailedRuns,
                    AggregateJson = JsonSerializer.Serialize(score.Aggregate, JsonOptions)
                }).ExecuteCommandAsync();
            }

            foreach (var run in result.Runs)
            {
                await db.Insertable(new OptimizationRunEvidenceEntity
                {
                    ExperimentId = result.ExperimentId,
                    PolicyId = run.PolicyId,
                    PolicyHash = run.PolicyHash,
                    LoadCase = run.LoadCase.ToString(),
                    Seed = run.Seed,
                    DeterminismRound = run.DeterminismRound,
                    ScenarioHash = run.ScenarioHash,
                    FinalStateHash = run.FinalStateHash,
                    EvidenceHash = run.EvidenceHash,
                    HardConstraintsSatisfied = run.HardConstraintsSatisfied,
                    FailureReason = run.FailureReason,
                    MetricsJson = JsonSerializer.Serialize(run.Metrics, JsonOptions),
                    StageEvidenceJson = JsonSerializer.Serialize(run.StageEvidence, JsonOptions)
                }).ExecuteCommandAsync();
            }

            definitionRow.Status = "Completed";
            definitionRow.CompletedAtUtc = DateTime.UtcNow;
            await db.Updateable(definitionRow).ExecuteCommandAsync();
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
        var rows = await db.Queryable<OptimizationExperimentEntity>()
            .OrderBy(x => x.CreatedAtUtc, OrderByType.Desc)
            .Take(limit)
            .ToListAsync();
        if (rows.Count == 0) return [];
        var ids = rows.Select(static row => row.ExperimentId).ToArray();
        var resultRows = await db.Queryable<OptimizationExperimentResultEntity>()
            .Where(x => ids.Contains(x.ExperimentId))
            .ToListAsync();
        var byExperiment = resultRows.ToDictionary(x => x.ExperimentId, StringComparer.Ordinal);
        return rows.Select(row =>
        {
            byExperiment.TryGetValue(row.ExperimentId, out var result);
            return new OptimizationExperimentSummary(
                row.ExperimentId,
                row.DefinitionHash,
                row.SoftwareHead,
                row.Status,
                row.CreatedAtUtc,
                row.CompletedAtUtc,
                result?.EvidenceHash,
                result?.ControlWriteAllowed ?? false,
                result?.AutoProductionPolicyReplacementAllowed ?? false);
        }).ToArray();
    }

    public async Task<OptimizationRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = CreateDb();
        var definitions = await db.Queryable<OptimizationExperimentEntity>().ToListAsync();
        var results = await db.Queryable<OptimizationExperimentResultEntity>().ToListAsync();
        var runRows = await db.Queryable<OptimizationRunEvidenceEntity>().ToListAsync();
        var errors = new List<string>();
        var definitionById = new Dictionary<string, OptimizationExperimentDefinition>(StringComparer.Ordinal);

        foreach (var row in definitions)
        {
            try
            {
                var definition = JsonSerializer.Deserialize<OptimizationExperimentDefinition>(row.DefinitionJson, JsonOptions)
                    ?? throw new InvalidOperationException("DefinitionJson is empty.");
                DigitalTwinOptimizer.ValidateDefinition(definition);
                if (!string.Equals(definition.DefinitionHash, row.DefinitionHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(definition.SoftwareHead, row.SoftwareHead, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(definition.ScenarioEvidenceHash, row.ScenarioEvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(definition.TopologyEvidenceHash, row.TopologyEvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(definition.OrderDatasetEvidenceHash, row.OrderDatasetEvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(definition.ObjectiveWeightsEvidenceHash, row.ObjectiveWeightsEvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(definition.ConstraintProfileHash, row.ConstraintProfileHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Persisted definition evidence/head mismatch.");
                definitionById[row.ExperimentId] = definition;
            }
            catch (Exception ex)
            {
                errors.Add($"{row.ExperimentId}: {ex.Message}");
            }
        }

        foreach (var row in results)
        {
            try
            {
                if (row.ControlWriteAllowed || row.AutoProductionPolicyReplacementAllowed || row.ProductionAutomationAllowed ||
                    !OptimizationHash.IsSha256(row.EvidenceHash))
                    throw new InvalidOperationException("persisted result violates zero-control evidence invariants.");
                var result = JsonSerializer.Deserialize<OptimizationExperimentResult>(row.ResultJson, JsonOptions)
                    ?? throw new InvalidOperationException("ResultJson is empty.");
                ValidateResultEvidence(result);
                if (!definitionById.TryGetValue(row.ExperimentId, out var definition))
                    throw new InvalidOperationException("matching valid definition is unavailable.");
                var recomputed = OptimizationHash.ComputeResultEvidenceHash(definition, result.Runs);
                if (!string.Equals(recomputed, row.EvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(recomputed, result.EvidenceHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("persisted result evidence hash mismatch.");
                var persistedRunCount = runRows.Count(run => string.Equals(run.ExperimentId, row.ExperimentId, StringComparison.Ordinal));
                if (persistedRunCount != result.Runs.Count)
                    throw new InvalidOperationException($"persisted run evidence count mismatch: expected {result.Runs.Count}, actual {persistedRunCount}.");
            }
            catch (Exception ex)
            {
                errors.Add($"{row.ExperimentId}: {ex.Message}");
            }
        }

        foreach (var run in runRows)
        {
            if (!OptimizationHash.IsSha256(run.PolicyHash) || !OptimizationHash.IsSha256(run.ScenarioHash) ||
                !OptimizationHash.IsSha256(run.FinalStateHash) || !OptimizationHash.IsSha256(run.EvidenceHash) ||
                run.DeterminismRound is < 1 or > OptimizationGovernance.DeterminismRoundsPerInput)
                errors.Add($"{run.ExperimentId}/{run.PolicyId}/{run.LoadCase}/{run.Seed}/{run.DeterminismRound}: invalid run evidence row.");
        }

        return new OptimizationRecoveryResult(definitions.Count, results.Count, errors.Count, errors.Count == 0, errors);
    }

    private static void ValidateResultEvidence(OptimizationExperimentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!OptimizationHash.IsSha256(result.DefinitionHash) || !OptimizationHash.IsSha256(result.EvidenceHash) ||
            result.ControlWriteAllowed || result.AutoProductionPolicyReplacementAllowed || result.ProductionAutomationAllowed)
            throw new InvalidOperationException("Optimization result violates governed evidence/control invariants.");
        if (result.Ranking.Count < OptimizationGovernance.MinimumCandidateCount)
            throw new InvalidOperationException("Optimization result does not contain the minimum governed candidate count.");
        if (result.Runs.Count == 0 || result.Runs.Any(static run => !OptimizationHash.IsSha256(run.EvidenceHash) ||
                                                       !OptimizationHash.IsSha256(run.ScenarioHash) ||
                                                       !OptimizationHash.IsSha256(run.FinalStateHash)))
            throw new InvalidOperationException("Optimization result contains invalid run evidence.");
        if (result.Ranking.Any(static score => !OptimizationHash.IsSha256(score.PolicyHash) ||
                                                   !double.IsFinite(score.Score)))
            throw new InvalidOperationException("Optimization ranking contains invalid policy evidence or non-finite score.");
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
