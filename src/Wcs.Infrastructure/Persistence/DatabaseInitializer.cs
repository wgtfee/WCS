namespace Wcs.Infrastructure.Persistence;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

/// <summary>
/// 数据库初始化接口
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// 确保数据库和所有表已存在，不存在则自动创建
    /// </summary>
    Task<bool> EnsureDatabaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 数据库初始化器 - 使用 EF Core EnsureCreated 自动建库建表
/// </summary>
public class DatabaseInitializer : IDatabaseInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(string connectionString, ILogger<DatabaseInitializer> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> EnsureDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var builder = new DbContextOptionsBuilder<WcsDbContext>();
        builder.UseSqlServer(_connectionString);

        using var context = new WcsDbContext(builder.Options);

        _logger.LogInformation("检查数据库状态...");

        // 第一步: 尝试 EnsureCreated
        var created = await context.Database.EnsureCreatedAsync(cancellationToken);

        if (created)
        {
            _logger.LogInformation("数据库和所有数据表已创建完成");
            return true;
        }

        // 第二步: EnsureCreated 返回 false，检查是否能连接
        var canConnect = await context.Database.CanConnectAsync(cancellationToken);

        if (!canConnect)
        {
            // 数据库完全不存在且创建失败，尝试手动创建
            _logger.LogWarning("数据库不存在且自动创建失败，尝试手动创建...");
            await CreateDatabaseIfNotExistsAsync(cancellationToken);

            // 再次尝试 EnsureCreated
            created = await context.Database.EnsureCreatedAsync(cancellationToken);
            if (created)
            {
                _logger.LogInformation("手动创建数据库和表完成");
                return true;
            }

            throw new InvalidOperationException("无法创建数据库，请检查连接字符串和权限");
        }

        // 第三步: 连接成功但 EnsureCreated 返回 false
        // 可能是库存在但无表（部分初始化场景）
        var creator = context.Database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
        var hasTables = await creator.HasTablesAsync(cancellationToken);

        if (!hasTables)
        {
            _logger.LogInformation("数据库已存在但无数据表，正在创建表结构...");
            await creator.CreateTablesAsync(cancellationToken);
            _logger.LogInformation("数据表创建完成");
            return true;
        }

        _logger.LogInformation("数据库已存在且表结构完整，跳过初始化");
        return false;
    }

    /// <summary>
    /// 手动创建数据库（兜底方案）
    /// </summary>
    private async Task CreateDatabaseIfNotExistsAsync(CancellationToken cancellationToken)
    {
        // 从连接字符串中提取数据库名
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.InitialCatalog;
        builder.InitialCatalog = "master"; // 连接到 master 来创建数据库

        using var masterConn = new SqlConnection(builder.ConnectionString);
        await masterConn.OpenAsync(cancellationToken);

        var checkCmd = masterConn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name = @dbName";
        checkCmd.Parameters.AddWithValue("@dbName", databaseName);

        var exists = (int)await checkCmd.ExecuteScalarAsync(cancellationToken) > 0;

        if (!exists)
        {
            var createCmd = masterConn.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE [{databaseName}]";
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("数据库 [{DbName}] 创建成功", databaseName);
        }
    }
}
