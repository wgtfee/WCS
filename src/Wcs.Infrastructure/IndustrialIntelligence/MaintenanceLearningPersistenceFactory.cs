namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;
using Wcs.MaintenanceLearning;

public sealed class MaintenanceLearningPersistenceFactory
{
    private static readonly object SchemaSync = new();
    private static readonly HashSet<string> InitializedConnectionStrings = new(StringComparer.Ordinal);
    private readonly string _connectionString;

    public MaintenanceLearningPersistenceFactory(string connectionString)
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
            MaintenanceLearningSchema.Ensure(db);
            InitializedConnectionStrings.Add(_connectionString);
        }
    }

    public SqlMaintenanceLearningStore CreateStore() => new(_connectionString);
    public IMaintenanceLearningRecovery CreateRecovery() => CreateStore();
    public IMaintenanceEvaluationWindowStore CreateEvaluationWindowStore() => new SqlMaintenanceEvaluationWindowStore(_connectionString);
    public IMaintenanceCausalEvidenceStore CreateCausalEvidenceStore() => new SqlMaintenanceCausalEvidenceStore(_connectionString);
}
