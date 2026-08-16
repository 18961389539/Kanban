using Kanban.Collector.Core.Models;
using Kanban.Collector.Core.Services;
using Microsoft.EntityFrameworkCore;
using Kanban.Collector.Core.Entities;

namespace Kanban.Collector.Core.Data;

/// <summary>
/// 生产快照数据库上下文（独立数据库文件 production_logs.db）。
/// </summary>
public class ProductionLogDbContext : KanbanDbContextBase
{
    public DbSet<ProductionLog> ProductionLogs => Set<ProductionLog>();

    public ProductionLogDbContext(AppSettings appSettings) : base(appSettings, "production_logs.db") { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductionLog>(e =>
        {
            e.HasKey(p => p.Id);
            // 复合索引：QueryProductionLogs 高频按 DeviceId + Timestamp 范围过滤+排序
            e.HasIndex(p => new { p.DeviceId, p.Timestamp });
            e.HasIndex(p => p.DeviceId);
            e.HasIndex(p => p.Timestamp);
            // WorkOrderId 索引：按工单统计产量时的高频查询路径
            e.HasIndex(p => p.WorkOrderId);
            // EventId 唯一索引：恢复文件回放幂等键（同 EventId 不重复落库）
            e.HasIndex(p => p.EventId).IsUnique();

            // 字符串字段显式约束：让 EnsureCreated 生成的 schema 有 NOT NULL 约束 + 长度限制，
            // 并由 EF Core Migrations 固化为数据库约束。
            e.Property(p => p.DeviceId).IsRequired().HasMaxLength(64);
            e.Property(p => p.DeviceName).HasMaxLength(128);
            e.Property(p => p.ShiftName).HasMaxLength(64);
            // WorkOrderId nullable：未关联工单时为 NULL，老数据兼容
            e.Property(p => p.WorkOrderId);
            // EventId nullable：兼容旧数据；GUID 以 TEXT 存储
            e.Property(p => p.EventId);
        });
    }
}
