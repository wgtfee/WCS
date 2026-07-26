namespace Wcs.Infrastructure.AnomalyDetection.MachineLearning;

using Microsoft.Extensions.Hosting;
using SqlSugar;
using Wcs.Core.AnomalyDetection.MachineLearning;

/// <summary>在 ML 后台服务启动前确保治理表和查询索引存在。</summary>
public sealed class PlcMlGovernanceSchemaService : IHostedService
{
    private readonly string _connectionString;

    public PlcMlGovernanceSchemaService(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });
        db.CodeFirst.InitTables(
            typeof(PlcMlCandidateEntity),
            typeof(PlcMlModelGovernanceEntity),
            typeof(PlcMlDriftSnapshotEntity));
        db.Ado.ExecuteCommand(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcMlCandidate_ProfileReview' AND object_id = OBJECT_ID('Wcs_PlcMlCandidate'))
    CREATE INDEX IX_Wcs_PlcMlCandidate_ProfileReview ON Wcs_PlcMlCandidate(ProfileId, ReviewDecision, DetectedUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcMlCandidate_Model' AND object_id = OBJECT_ID('Wcs_PlcMlCandidate'))
    CREATE INDEX IX_Wcs_PlcMlCandidate_Model ON Wcs_PlcMlCandidate(ProfileId, ModelVersion, DetectedUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcMlModelGovernance_Profile' AND object_id = OBJECT_ID('Wcs_PlcMlModelGovernance'))
    CREATE INDEX IX_Wcs_PlcMlModelGovernance_Profile ON Wcs_PlcMlModelGovernance(ProfileId, ApprovalStatus, RequestedUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Wcs_PlcMlDriftSnapshot_Profile' AND object_id = OBJECT_ID('Wcs_PlcMlDriftSnapshot'))
    CREATE INDEX IX_Wcs_PlcMlDriftSnapshot_Profile ON Wcs_PlcMlDriftSnapshot(ProfileId, CalculatedUtc);");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
