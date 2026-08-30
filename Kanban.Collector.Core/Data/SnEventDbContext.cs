using Kanban.Collector.Core.Entities;
using Kanban.Collector.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Kanban.Collector.Core.Data;

/// <summary>
/// 序列号事件数据库上下文（独立数据库文件 sn_events.db）。
/// 单表 + 索引：按 SN 精确查询（追溯入口）、按工单查明细、按设备+时间范围查。
/// 全新数据库走 EnsureCreated 建表（sn_events.db 无历史版本，不参与 EF 迁移）。
/// </summary>
public class SnEventDbContext : KanbanDbContextBase
{
    public DbSet<SnEventRecord> SnEvents => Set<SnEventRecord>();

    public SnEventDbContext(AppSettings appSettings) : base(appSettings, "sn_events.db") { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SnEventRecord>(e =>
        {
            e.HasKey(p => p.Id);
            // 追溯入口：按 SN 精确查询（高频）
            e.HasIndex(p => p.Sn);
            // 工单详情 SN 明细：按工单聚合
            e.HasIndex(p => p.WorkOrderId);
            // 时间范围 + 设备过滤（历史查询页）
            e.HasIndex(p => new { p.DeviceId, p.Timestamp });
            e.HasIndex(p => p.Timestamp);

            e.Property(p => p.Sn).IsRequired().HasMaxLength(128);
            e.Property(p => p.DeviceId).IsRequired().HasMaxLength(64);
            e.Property(p => p.DeviceName).HasMaxLength(128);
            e.Property(p => p.ShiftName).HasMaxLength(64);
            e.Property(p => p.Source).HasMaxLength(32);
            e.Property(p => p.SourceId).HasMaxLength(64);
            e.Property(p => p.BatchNo).HasMaxLength(64);
        });
    }
}
