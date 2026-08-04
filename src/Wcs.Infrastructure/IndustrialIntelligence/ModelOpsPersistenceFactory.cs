namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;
using Wcs.ModelOps;

public sealed class ModelOpsPersistenceFactory
{
    private static readonly object SchemaSync = new();
    private static readonly HashSet<string> InitializedConnectionStrings = new(StringComparer.Ordinal);
    private readonly string _connectionString;

    public ModelOpsPersistenceFactory(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public void EnsureSchema()
    {
        lock (SchemaSync)
        {
            if (InitializedConnectionStrings.Contains(_connectionString))
                return;

            using var db = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = _connectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true
            });
            ModelOpsSchema.Ensure(db);
            InitializedConnectionStrings.Add(_connectionString);
        }
    }

    public IModelRegistry CreateRegistry() => new SqlModelRegistry(_connectionString);

    public IModelDeploymentStore CreateDeploymentStore() => new SqlModelDeploymentStore(_connectionString);

    public IModelOpsAuditJournal CreateAuditJournal() => new SqlModelOpsAuditJournal(_connectionString);

    public IModelEvaluationStore CreateEvaluationStore() => new SqlModelEvaluationStore(_connectionString);

    public IModelDriftStore CreateDriftStore() => new SqlModelDriftStore(_connectionString);

    public IModelDeploymentGovernanceManager CreateDeploymentManager() =>
        new PersistentModelDeploymentManager(
            CreateRegistry(),
            CreateDeploymentStore(),
            CreateAuditJournal());

    public ModelDeploymentRecoveryService CreateRecoveryService() =>
        new(CreateRegistry(), CreateDeploymentStore());
}
