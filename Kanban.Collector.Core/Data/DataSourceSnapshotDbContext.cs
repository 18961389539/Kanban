using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Kanban.Collector.Core.Data;

/// <summary>
/// 数据源快照数据库上下文（独立数据库文件 datasource_snapshots.db）。
/// 单表 + DeviceId 索引：快照按设备聚合查询（聚合是查询维度，不做每设备物理分表）。
/// </summary>
public class DataSourceSnapshotDbContext : KanbanDbContextBase
{
    public DbSet<DataSourceSnapshotRecord> DataSourceSnapshots => Set<DataSourceSnapshotRecord>();

    public DataSourceSnapshotDbContext(AppSettings appSettings) : base(appSettings, "datasource_snapshots.db") { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DataSourceSnapshotRecord>(e =>
        {
            e.HasKey(p => p.Id);
            // 复合索引：设备详情页按 DeviceId + Timestamp 范围过滤+排序的高频路径
            e.HasIndex(p => new { p.DeviceId, p.Timestamp });
            e.HasIndex(p => p.DeviceId);
            e.HasIndex(p => p.Timestamp);
            // 按源过滤（趋势图按单源查询）的路径
            e.HasIndex(p => new { p.SourceId, p.Timestamp });

            // 字符串字段显式约束：让 EnsureCreated 生成的 schema 有 NOT NULL 约束 + 长度限制，
            // 并由 EF Core Migrations 固化为数据库约束。
            e.Property(p => p.DeviceId).IsRequired().HasMaxLength(64);
            e.Property(p => p.DeviceName).HasMaxLength(128);
            e.Property(p => p.SourceId).IsRequired().HasMaxLength(64);
            e.Property(p => p.SourceName).HasMaxLength(128);
            e.Property(p => p.SourceType).HasMaxLength(64);
            e.Property(p => p.Unit).HasMaxLength(32);
            e.Property(p => p.ShiftName).HasMaxLength(64);
            e.Property(p => p.StringValue).HasMaxLength(1024);
        });
    }
}