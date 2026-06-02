namespace Wcs.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// WCS 数据库上下文 - 只存活跃数据和历史记录
/// </summary>
public class WcsDbContext : DbContext
{
    public WcsDbContext(DbContextOptions<WcsDbContext> options) : base(options) { }

    // Runtime - 活跃数据
    public DbSet<TaskRuntimeEntity> TaskRuntimes => Set<TaskRuntimeEntity>();
    public DbSet<DeviceRuntimeEntity> DeviceRuntimes => Set<DeviceRuntimeEntity>();
    public DbSet<AlarmRuntimeEntity> AlarmRuntimes => Set<AlarmRuntimeEntity>();

    // History - 历史记录
    public DbSet<TaskHistoryEntity> TaskHistories => Set<TaskHistoryEntity>();
    public DbSet<AlarmHistoryEntity> AlarmHistories => Set<AlarmHistoryEntity>();

    // Event - 只追加
    public DbSet<TaskEventEntity> TaskEvents => Set<TaskEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskRuntimeEntity>(e =>
        {
            e.HasKey(t => t.TaskId);
            e.Property(t => t.TaskId).HasMaxLength(64);
            e.Property(t => t.Status).HasMaxLength(32);
            e.Property(t => t.RouteId).HasMaxLength(64);
        });

        modelBuilder.Entity<DeviceRuntimeEntity>(e =>
        {
            e.HasKey(d => d.DeviceId);
            e.Property(d => d.DeviceId).HasMaxLength(64);
            e.Property(d => d.Status).HasMaxLength(32);
        });

        modelBuilder.Entity<AlarmRuntimeEntity>(e =>
        {
            e.HasKey(a => a.AlarmId);
            e.Property(a => a.AlarmId).HasMaxLength(64);
            e.Property(a => a.AlarmCode).HasMaxLength(64);
            e.Property(a => a.Status).HasMaxLength(32);
            e.Property(a => a.Level).HasMaxLength(16);
        });

        modelBuilder.Entity<TaskHistoryEntity>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.TaskId).HasMaxLength(64);
            e.HasIndex(t => t.TaskId);
            e.HasIndex(t => t.StartTime);
        });

        modelBuilder.Entity<AlarmHistoryEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.AlarmCode).HasMaxLength(64);
            e.HasIndex(a => a.AlarmCode);
        });

        modelBuilder.Entity<TaskEventEntity>(e =>
        {
            e.HasKey(te => te.Id);
            e.Property(te => te.TaskId).HasMaxLength(64);
            e.Property(te => te.EventType).HasMaxLength(64);
            e.HasIndex(te => te.TaskId);
            e.HasIndex(te => te.CreateTime);
        });
    }
}

// --- Runtime Entities ---

public class TaskRuntimeEntity
{
    public string TaskId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string? RouteId { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Parameters { get; set; }
}

public class DeviceRuntimeEntity
{
    public string DeviceId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime LastUpdateTime { get; set; }
    public string? Properties { get; set; }
}

public class AlarmRuntimeEntity
{
    public string AlarmId { get; set; } = string.Empty;
    public string AlarmCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTime OccurTime { get; set; }
    public DateTime? RecoverTime { get; set; }
}

// --- History Entities ---

public class TaskHistoryEntity
{
    public long Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string? RouteId { get; set; }
    public int Priority { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class AlarmHistoryEntity
{
    public long Id { get; set; }
    public string AlarmCode { get; set; } = string.Empty;
    public string? Level { get; set; }
    public string? Message { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

// --- Event Entities ---

public class TaskEventEntity
{
    public long Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? Payload { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.UtcNow;
}
