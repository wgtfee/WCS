namespace Wcs.Infrastructure.IndustrialIntelligence;

using SqlSugar;
using Wcs.FeatureCenter;

/// <summary>
/// Creates short-lived Feature Center persistence services. Schema initialization is explicit and
/// never belongs on the deterministic WCS control path.
/// </summary>
public sealed class FeatureCenterPersistenceFactory
{
    private readonly string _connectionString;

    public FeatureCenterPersistenceFactory(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("Connection string is required.", nameof(connectionString))
            : connectionString;
    }

    public void EnsureSchema()
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        });
        FeatureCenterSchema.Ensure(db);
    }

    public IFeatureDefinitionRegistry CreateDefinitionRegistry() => new SqlFeatureDefinitionRegistry(_connectionString);
    public IFeatureSchemaRegistry CreateSchemaRegistry() => new SqlFeatureSchemaRegistry(_connectionString, CreateDefinitionRegistry());
    public IFeatureSnapshotStore CreateSnapshotStore() => new SqlFeatureSnapshotStore(_connectionString);
    public IFeatureQualityEventStore CreateQualityEventStore() => new SqlFeatureQualityEventStore(_connectionString);
    public IFeatureDatasetStore CreateDatasetStore() => new SqlFeatureDatasetStore(_connectionString);
    public IFeatureLineageStore CreateLineageStore() => new SqlFeatureLineageStore(_connectionString);
}
